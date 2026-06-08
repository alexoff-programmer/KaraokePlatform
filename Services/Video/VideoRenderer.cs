using System.Diagnostics;

namespace KaraokePlatform.Services.Video;

public class VideoRenderer
{
    private readonly ILogger<VideoRenderer> _logger;
    private readonly string _ffmpegPath;

    public VideoRenderer(ILogger<VideoRenderer> logger, IWebHostEnvironment environment)
    {
        _logger = logger;
        _ffmpegPath = Path.Combine(environment.ContentRootPath, "Models", "ffmpeg.exe");
    }

    public async Task RenderKaraokeVideoAsync(string audioPath, string assSubtitlesPath, string outputVideoPath)
    {
        if (!File.Exists(audioPath)) throw new FileNotFoundException("Исходный аудиофайл не найден.");
        if (!File.Exists(assSubtitlesPath)) throw new FileNotFoundException("Файл субтитров не найден.");
        if (!File.Exists(_ffmpegPath)) throw new FileNotFoundException($"FFmpeg не найден по пути: {_ffmpegPath}");

        // Экранируем пути для FFmpeg (особенно важно для Windows, где обратные слэши)
        // Для фильтра subtitles в FFmpeg путь должен быть с прямыми слэшами и экранированными двоеточиями
        string escapedAssPath = assSubtitlesPath.Replace("\\", "/").Replace(":", "\\:");

        // Команда FFmpeg:
        // 1. -f lavfi -i color=... -> Генерируем сплошной темный фон (цвета #1a1a2e) в вертикальном формате 1080x1920
        // 2. -i "{audioPath}" -> Подкладываем оригинальный звук песни
        // 3. -vf "subtitles=..." -> Накладываем (впекаем) наши караоке-субтитры поверх видео
        // 4. -shortest -> Завершаем видео сразу, как только закончится аудиофайл
        string arguments = $"-f lavfi -i color=c=0x1a1a2e:s=1080x1920:r=30 -i \"{audioPath}\" " +
                           $"-vf \"subtitles='{escapedAssPath}'\" " +
                           $"-c:v libx264 -preset ultrafast -crf 23 -c:a aac -shortest -y \"{outputVideoPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.LogInformation("FFmpeg запускает рендеринг видео...");

        using var process = new Process { StartInfo = startInfo };

        process.Start();

        // Читаем логи FFmpeg асинхронно, чтобы процесс не завис из-за переполнения буфера
        string errorLog = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            _logger.LogError($"FFmpeg завершился с ошибкой (Код {process.ExitCode}). Лог:\n{errorLog}");
            throw new Exception($"Ошибка FFmpeg при сборке видео. Код возврата: {process.ExitCode}");
        }

        _logger.LogInformation("Рендеринг видео успешно завершен!");
    }
}