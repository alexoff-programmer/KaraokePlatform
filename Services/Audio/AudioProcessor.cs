using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KaraokePlatform.Services.Audio.Interfaces;

namespace KaraokePlatform.Services.Audio;

public class AudioProcessor : IAudioProcessor
{
    private static readonly Regex ProgressRegex = new Regex(@"(\d+)(?:.\d+)?%", RegexOptions.Compiled);

    // ИСПРАВЛЕНО: Добавлен параметр instrumentalOutputPath и качество разделения
    public void ConvertAndFilterMp3ToWav(string inputPath, string outputPath, string instrumentalOutputPath, string quality = "medium", Action<int>? onProgress = null)
    {
        string tempOutputDir = Path.Combine(Path.GetTempPath(), "KaraokeTemp", $"sep_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempOutputDir);

        try
        {
            string modelName = "Kim_Vocal_2.onnx";
            string modelArgs = "--mdx_overlap 0.25";

            if (quality.Equals("high", StringComparison.OrdinalIgnoreCase))
            {
                modelName = "model_bs_roformer_ep_317_sdr_12.9755.ckpt";
                modelArgs = ""; // Использовать оптимальные встроенные дефолты для Roformer (MDXC)
            }
            else if (quality.Equals("low", StringComparison.OrdinalIgnoreCase))
            {
                modelName = "Kim_Vocal_2.onnx";
                modelArgs = "--mdx_overlap 0.05"; // Очень быстрый оверлап для экономии CPU ресурсов
            }

            var arguments = $"\"{inputPath}\" -m {modelName} {modelArgs} --output_dir \"{tempOutputDir}\" --output_format WAV";

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
            var stderrBuilder = new StringBuilder();

            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data == null) return;
                stderrBuilder.AppendLine(args.Data);

                if (onProgress != null)
                {
                    var match = ProgressRegex.Match(args.Data);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int percent))
                    {
                        int overallPercent = 10 + (percent * 15) / 100;
                        onProgress.Invoke(overallPercent);
                    }
                }
            };

            process.Start();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"ИИ-разделение аудио завершилось с ошибкой (Код {process.ExitCode}): {stderrBuilder}");
            }

            onProgress?.Invoke(25);

            string inputFileNameNoExt = Path.GetFileNameWithoutExtension(inputPath);
            var outputDirInfo = new DirectoryInfo(tempOutputDir);

            var vocalsFile = outputDirInfo.GetFiles($"*{inputFileNameNoExt}*(Vocals)*.wav").FirstOrDefault();
            // НОВОЕ: Находим файл инструментала
            var instrumentalFile = outputDirInfo.GetFiles($"*{inputFileNameNoExt}*(Instrumental)*.wav").FirstOrDefault();

            if (vocalsFile == null || !vocalsFile.Exists)
            {
                throw new FileNotFoundException($"Файл вокала не найден в папке {tempOutputDir}. Лог утилиты: {stderrBuilder}");
            }
            if (instrumentalFile == null || !instrumentalFile.Exists)
            {
                throw new FileNotFoundException($"Файл инструментала (минусовки) не найден в папке {tempOutputDir}. Лог утилиты: {stderrBuilder}");
            }

            // НОВОЕ: Перемещаем чистую минусовку в её постоянное место (которое потом отдадим в VideoRenderer)
            if (File.Exists(instrumentalOutputPath)) File.Delete(instrumentalOutputPath);
            File.Move(instrumentalFile.FullName, instrumentalOutputPath);

            // Пережимаем в Моно 16кГц для Whisper
            DownsampleToWhisperFormat(vocalsFile.FullName, outputPath);
        }
        finally
        {
            if (Directory.Exists(tempOutputDir))
            {
                try { Directory.Delete(tempOutputDir, true); } catch { }
            }
        }
    }

    private void DownsampleToWhisperFormat(string inputWav, string outputWav)
    {
        // amix=inputs=1: смешивает каналы одного входного потока без потери громкости, защищая от пустого канала
        // highpass=f=120: срезает низкочастотные ИИ-артефакты/гул разделения вокала ниже 120 Гц, чтобы они не путали Whisper
        // volume=1.5: делает вокал громче и четче для лучшего распознавания
        var audioFilter = "amix=inputs=1,highpass=f=120,volume=1.5";
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

        // Вычитываем логи ошибок FFmpeg в реальном времени
        string ffmpegErrors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // ЖЕЛЕЗНАЯ ВАЛИДАЦИЯ: Проверяем код возврата ОС и физическое наличие файла на диске
        if (process.ExitCode != 0 || !File.Exists(outputWav))
        {
            throw new Exception(
                $"FFmpeg аварийно завершил работу (Код: {process.ExitCode}).\n" +
                $"Лог ошибок утилиты:\n{ffmpegErrors}"
            );
        }
    }

}