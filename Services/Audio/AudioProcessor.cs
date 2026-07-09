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

    public void ConvertAndFilterMp3ToWav(
        string inputPath, 
        string outputPath, 
        string instrumentalOutputPath, 
        string quality = "medium", 
        Action<int>? onProgress = null)
    {
        string tempOutputDir = Path.Combine(Path.GetTempPath(), "KaraokeTemp", $"sep_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempOutputDir);

        string tempStereoWav = Path.Combine(tempOutputDir, "input_stereo.wav");
        string absoluteModelPath = Path.Combine(_environment.ContentRootPath, "Models", "audio_models", "Kim_Vocal_2.onnx");

        try
        {
            onProgress?.Invoke(5); // Подготовка
            
            // 1. Предварительно декодируем/конвертируем входной файл в 44100Hz stereo WAV
            PreConvertAudioToStereo44100Wav(inputPath, tempStereoWav);
            onProgress?.Invoke(10);

            // 2. Инициализируем и запускаем разделение вокала на C# через ONNX Runtime
            SimpleAudioSeparationService? separator = null;
            try
            {
                if (quality.Equals("high", StringComparison.OrdinalIgnoreCase))
                {
                    var options = new SimpleSeparationOptions
                    {
                        Model = InternalModel.Best,
                        OutputDirectory = tempOutputDir,
                        EnableGPU = false
                    };
                    separator = new SimpleAudioSeparationService(options);
                    separator.Initialize();
                }
            }
            catch
            {
                // Откат на локальную Kim_Vocal_2, если оффлайн или ошибка скачивания
                separator?.Dispose();
                separator = null;
            }

            if (separator == null)
            {
                var options = new SimpleSeparationOptions
                {
                    ModelPath = absoluteModelPath,
                    OutputDirectory = tempOutputDir,
                    NFft = 7680,
                    DimF = 3072,
                    DimT = 8,
                    EnableGPU = false
                };
                separator = new SimpleAudioSeparationService(options);
                separator.Initialize();
            }

            using (separator)
            {
                if (onProgress != null)
                {
                    separator.ProgressChanged += (sender, progress) =>
                    {
                        // Масштабируем внутренний прогресс в диапазон 10-25% общего прогресса
                        int overallPercent = 10 + (int)(progress.OverallProgress * 15 / 100);
                        onProgress.Invoke(overallPercent);
                    };
                }

                var result = separator.Separate(tempStereoWav);

                onProgress?.Invoke(25);

                // 3. Валидируем результаты разделения
                if (!File.Exists(result.VocalsPath) || !File.Exists(result.InstrumentalPath))
                {
                    throw new FileNotFoundException("Файлы после разделения вокала не были созданы разделяющим сервисом.");
                }

                // 4. Перемещаем чистую минусовку (инструментал) в конечное место
                if (File.Exists(instrumentalOutputPath)) File.Delete(instrumentalOutputPath);
                File.Move(result.InstrumentalPath, instrumentalOutputPath);

                // 5. Пережимаем вокал в моно 16кГц с фильтрацией для Whisper
                DownsampleToWhisperFormat(result.VocalsPath, outputPath);
            }
        }
        finally
        {
            if (Directory.Exists(tempOutputDir))
            {
                try { Directory.Delete(tempOutputDir, true); } catch { }
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

    public float[] SliceSamples(float[] samples, ref TimeSpan start, TimeSpan end)
    {
        int startIndex = (int)(start.TotalSeconds * 16000);
        int endIndex = (int)(end.TotalSeconds * 16000);

        if (startIndex < 0) startIndex = 0;
        if (endIndex > samples.Length) endIndex = samples.Length;
        if (startIndex >= endIndex) return Array.Empty<float>();

        int segmentLength = endIndex - startIndex;
        var segmentSamples = new float[segmentLength];
        Array.Copy(samples, startIndex, segmentSamples, 0, segmentLength);

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