using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using KaraokePlatform.Services.Audio.Interfaces;
using Microsoft.AspNetCore.Hosting;
using OwnaudioNET.Features.Vocalremover;

namespace KaraokePlatform.Services.Audio;

public class AudioProcessor : IAudioProcessor
{
    private readonly IWebHostEnvironment _environment;

    public AudioProcessor(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    private static readonly System.Text.RegularExpressions.Regex ProgressRegex = new System.Text.RegularExpressions.Regex(@"(\d+)(?:.\d+)?%", System.Text.RegularExpressions.RegexOptions.Compiled);

    public void ConvertAndFilterMp3ToWav(
        string inputPath, 
        string outputPath, 
        string instrumentalOutputPath, 
        string quality = "medium", 
        Action<int>? onProgress = null)
    {
        string tempOutputDir = Path.Combine(Path.GetTempPath(), "KaraokeTemp", $"sep_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempOutputDir);

        try
        {
            onProgress?.Invoke(5); // Подготовка
            
            string modelName = "model_bs_roformer_ep_317_sdr_12.9755.ckpt";
            string modelFileDir = Path.Combine(_environment.ContentRootPath, "Models", "audio_models");
            string modelArgs = "--mdxc_overlap 8";

            var arguments = $"\"{inputPath}\" -m {modelName} --model_file_dir \"{modelFileDir}\" {modelArgs} --output_dir \"{tempOutputDir}\" --output_format WAV";

            var startInfo = new ProcessStartInfo
            {
                FileName = "audio-separator",
                Arguments = arguments,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process();
            process.StartInfo = startInfo;
            var stderrBuilder = new System.Text.StringBuilder();

            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data == null) return;
                stderrBuilder.AppendLine(args.Data);

                if (onProgress != null)
                {
                    var match = ProgressRegex.Match(args.Data);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int percent))
                    {
                        int overallPercent = 5 + (percent * 20) / 100;
                        onProgress.Invoke(overallPercent);
                    }
                }
            };

            process.Start();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"ИИ-разделение аудио через audio-separator завершилось с ошибкой (Код {process.ExitCode}): {stderrBuilder}");
            }

            onProgress?.Invoke(25);

            string inputFileNameNoExt = Path.GetFileNameWithoutExtension(inputPath);
            var outputDirInfo = new DirectoryInfo(tempOutputDir);

            var vocalsFile = outputDirInfo.GetFiles($"*{inputFileNameNoExt}*(Vocals)*.wav").FirstOrDefault();
            var instrumentalFile = outputDirInfo.GetFiles($"*{inputFileNameNoExt}*(Instrumental)*.wav").FirstOrDefault();

            if (vocalsFile == null || !vocalsFile.Exists)
            {
                throw new FileNotFoundException($"Файл вокала не найден в папке {tempOutputDir}. Лог утилиты: {stderrBuilder}");
            }
            if (instrumentalFile == null || !instrumentalFile.Exists)
            {
                throw new FileNotFoundException($"Файл инструментала (минусовки) не найден в папке {tempOutputDir}. Лог утилиты: {stderrBuilder}");
            }

            // Перемещаем чистую минусовку в её постоянное место
            if (File.Exists(instrumentalOutputPath)) File.Delete(instrumentalOutputPath);
            File.Move(instrumentalFile.FullName, instrumentalOutputPath);

            // Пережимаем в Моно 16кГц для Whisper
            DownsampleToWhisperFormat(vocalsFile.FullName, outputPath);
        }
        finally
        {
            if (Directory.Exists(tempOutputDir))
            {
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        Directory.Delete(tempOutputDir, true);
                        break;
                    }
                    catch
                    {
                        System.Threading.Thread.Sleep(200);
                    }
                }
            }
        }
    }

    private void PreConvertAudioToStereo44100Wav(string inputPath, string tempStereoWav)
    {
        var arguments = $"-y -i \"{inputPath}\" -ar 44100 -ac 2 -c:a pcm_s16le \"{tempStereoWav}\"";
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        if (process == null) throw new Exception("Не удалось запустить FFmpeg для предварительного декодирования.");

        string ffmpegErrors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(tempStereoWav))
        {
            throw new Exception($"FFmpeg декодер завершился с ошибкой (Код: {process.ExitCode}).\nЛог:\n{ffmpegErrors}");
        }
    }

    private void DownsampleToWhisperFormat(string inputWav, string outputWav)
    {
        // amix=inputs=1: смешивает каналы одного входного потока без потери громкости
        // highpass=f=120: срезает низкочастотные ИИ-артефакты ниже 120 Гц
        // equalizer: поднимает разборчивость речи на частоте 3000 Гц
        // volume=1.5: делает вокал громче и четче
        var audioFilter = "amix=inputs=1:normalize=0,highpass=f=120,equalizer=f=3000:width_type=h:width=2000:g=4,volume=1.5";
        var arguments = $"-y -i \"{inputWav}\" -af \"{audioFilter}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{outputWav}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);

        if (process == null)
        {
            throw new Exception("Не удалось запустить процесс FFmpeg.");
        }

        string ffmpegErrors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(outputWav))
        {
            throw new Exception(
                $"FFmpeg аварийно завершил работу (Код: {process.ExitCode}).\n" +
                $"Лог ошибок утилиты:\n{ffmpegErrors}"
            );
        }
    }

    public float[] SliceSamples(float[] samples, ref TimeSpan start, TimeSpan end, bool trimSilence = false)
    {
        int startIndex = (int)(start.TotalSeconds * 16000);
        int endIndex = (int)(end.TotalSeconds * 16000);

        if (startIndex < 0) startIndex = 0;
        if (endIndex > samples.Length) endIndex = samples.Length;
        if (startIndex >= endIndex) return Array.Empty<float>();

        int segmentLength = endIndex - startIndex;
        var segmentSamples = new float[segmentLength];
        Array.Copy(samples, startIndex, segmentSamples, 0, segmentLength);

        if (!trimSilence)
        {
            return segmentSamples;
        }

        // Find silent start duration using 20ms frames and noise-gating
        int frameSize = 320;
        int numFrames = segmentLength / frameSize;
        int firstSpeechFrame = numFrames;

        double[] frameRmsDb = new double[numFrames];
        for (int f = 0; f < numFrames; f++)
        {
            double sumSq = 0;
            for (int i = 0; i < frameSize; i++)
            {
                float s = segmentSamples[f * frameSize + i];
                sumSq += s * s;
            }
            double rms = Math.Sqrt(sumSq / frameSize);
            frameRmsDb[f] = 20 * Math.Log10(rms + 1e-10);
        }

        // Noise gate: find first frame where energy exceeds -38dB and is sustained
        for (int f = 0; f < numFrames; f++)
        {
            if (frameRmsDb[f] >= -38.0)
            {
                int activeCount = 0;
                int checkLimit = Math.Min(10, numFrames - f);
                for (int offset = 0; offset < checkLimit; offset++)
                {
                    if (frameRmsDb[f + offset] >= -38.0)
                    {
                        activeCount++;
                    }
                }

                if (activeCount >= 5)
                {
                    firstSpeechFrame = f;
                    break;
                }
            }
        }

        double silentStartSec = firstSpeechFrame * 0.02;
        double trimSec = Math.Min(silentStartSec, 0.700);

        start = start.Add(TimeSpan.FromSeconds(trimSec));

        int trimSamples = (int)(trimSec * 16000);
        if (trimSamples > 0 && trimSamples < segmentSamples.Length)
        {
            var trimmedSamples = new float[segmentSamples.Length - trimSamples];
            Array.Copy(segmentSamples, trimSamples, trimmedSamples, 0, trimmedSamples.Length);
            return trimmedSamples;
        }

        return segmentSamples;
    }
}