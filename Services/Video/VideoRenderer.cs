using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace KaraokePlatform.Services.Video;

public class VideoRenderer
{
    private readonly ILogger<VideoRenderer> _logger;
    private readonly string _fontsFolder;

    public VideoRenderer(ILogger<VideoRenderer> logger, IWebHostEnvironment environment)
    {
        _logger = logger;
        _fontsFolder = Path.Combine(environment.ContentRootPath, "Fonts");
    }

    // ИСПРАВЛЕНО: Добавили параметры backgroundImagePath и videoFormat в метод
    public virtual async Task RenderKaraokeVideoAsync(string audioPath, string assSubtitlesPath, string outputVideoPath, string? backgroundImagePath = null, string? videoFormat = null)
    {
        if (!File.Exists(audioPath)) throw new FileNotFoundException("Исходный аудиофайл не найден.");
        if (!File.Exists(assSubtitlesPath)) throw new FileNotFoundException("Файл субтитров не найден.");

        bool isLandscape = videoFormat == "landscape";
        int videoW = isLandscape ? 1920 : 1080;
        int videoH = isLandscape ? 1080 : 1920;

        // Escape absolute paths for FFmpeg filter graph to avoid issues with colons and backslashes
        string escapedAssPath = assSubtitlesPath.Replace("\\", "/").Replace(":", "\\:").Replace("'", "'\\''");
        string escapedFontsDir = _fontsFolder.Replace("\\", "/").Replace(":", "\\:").Replace("'", "'\\''");

        double audioDurationSeconds;
        audioDurationSeconds = await GetAudioDurationAsync(audioPath);
        string durationStr = audioDurationSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        string arguments;

        bool hasBgImage = !string.IsNullOrEmpty(backgroundImagePath) && File.Exists(backgroundImagePath);

        if (!hasBgImage)
        {
            // ВАРИАНТ 1: Просто чёрный фон
            arguments = $"-f lavfi -t {durationStr} -i \"color=c=black:s={videoW}x{videoH}:r=30\" " +
                        $"-i \"{audioPath}\" " +
                        $"-filter_complex \"" +
                        $"[0:v]subtitles=filename='{escapedAssPath}':fontsdir='{escapedFontsDir}'[outv]\" " +
                        $"-map \"[outv]\" -map 1:a " +
                        $"-c:v libx264 -preset ultrafast -profile:v high -level:v 4.1 -pix_fmt yuv420p -crf 23 -c:a aac -y \"{outputVideoPath}\"";
        }
        else
        {
            // ВАРИАНТ 2: Картинка на фоне — кадрируем и масштабируем чтобы заполнить весь экран (crop-fill)
            arguments = $"-loop 1 -t {durationStr} -i \"{backgroundImagePath}\" " +
                        $"-i \"{audioPath}\" " +
                        $"-filter_complex \"" +
                        $"[0:v]scale={videoW}:{videoH}:force_original_aspect_ratio=increase," +
                        $"crop={videoW}:{videoH}," +
                        $"subtitles=filename='{escapedAssPath}':fontsdir='{escapedFontsDir}'[outv]\" " +
                        $"-map \"[outv]\" -map 1:a " +
                        $"-c:v libx264 -preset ultrafast -profile:v high -level:v 4.1 -pix_fmt yuv420p -crf 23 -c:a aac -y \"{outputVideoPath}\"";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Настройки шрифтов для libass (fallback на локальную папку Fonts/)
        startInfo.EnvironmentVariables["FONTCONFIG_FILE"] = Path.Combine(_fontsFolder, "fonts.conf");
        startInfo.EnvironmentVariables["FC_CONFIG_DIR"] = _fontsFolder;

        _logger.LogInformation("FFmpeg запускает рендеринг видео...");

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Читаем лог ошибок асинхронно, чтобы избежать зависания процесса
        string errorLog = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            _logger.LogError($"FFmpeg завершился с ошибкой (Код {process.ExitCode}). Лог:\n{errorLog}");
            throw new Exception($"Ошибка FFmpeg при сборке видео. Код возврата: {process.ExitCode}");
        }

        _logger.LogInformation("Рендеринг видео успешно завершен!");
    }

    private async Task<double> GetAudioDurationAsync(string audioPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffprobe",
            // v error глушит лишние логи, show_entries вытаскивает только длительность, of default убирает лишнюю разметку
            Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{audioPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            _logger.LogWarning($"Не удалось определить длительность аудио через ffprobe для файла {audioPath}. Используем дефолтные 180 сек.");
            return 180.0; // Дефолтное значение на случай сбоя
        }

        // Парсим результат в double, учитывая инвариантную культуру (точку в качестве разделителя)
        if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double duration))
        {
            return duration;
        }

        return 180.0;
    }
}