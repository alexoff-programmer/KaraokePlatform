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

    // ИСПРАВЛЕНО: Добавлен параметр instrumentalOutputPath
    public void ConvertAndFilterMp3ToWav(string inputPath, string outputPath, string instrumentalOutputPath, Action<int>? onProgress = null)
    {
        string tempOutputDir = Path.Combine(Path.GetTempPath(), "KaraokeTemp", $"sep_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempOutputDir);

        try
        {
            var arguments = $"\"{inputPath}\" -m Kim_Vocal_2.onnx --output_dir \"{tempOutputDir}\" --output_format WAV";

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
                        int overallPercent = (percent * 25) / 100;
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
        // НАСТРОЙКА ГЕЙТА:
        // agate=threshold=-40dB означает: всё, что тише -40 децибел, превращается в абсолютный ноль.
        // range=0: коэффициент ослабления (полное глушение).
        // attack=20: скорость срабатывания в мс (чтобы не проглотить начало первой буквы).
        // release=200: скорость закрытия гейта в мс (чтобы мягко затухало окончание слов).
        var audioFilter = "agate=threshold=-40dB:range=0:attack=20:release=200";
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