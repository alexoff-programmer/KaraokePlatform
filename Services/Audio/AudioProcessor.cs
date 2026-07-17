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
            
            // Используем загруженную пользователем ONNX модель bs_roformer
            string modelName = "bs_roformer_ep317_sdr12.9755_quantized_uint8.onnx";
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

            // Пережимаем вокал в Моно 16кГц для Whisper
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
        var audioFilter = "highpass=f=120,equalizer=f=3000:width_type=h:width=2000:g=4,volume=1.5";
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

        return segmentSamples;
    }
}