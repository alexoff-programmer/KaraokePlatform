using System.Diagnostics;

namespace KaraokePlatform.Services.Video;

public class VideoRenderer
{
    private readonly ILogger<VideoRenderer> _logger;
    private readonly string _ffmpegPath;
    private readonly string _fontsFolder;

    public VideoRenderer(ILogger<VideoRenderer> logger, IWebHostEnvironment environment)
    {
        _logger = logger;
        _ffmpegPath = Path.Combine(environment.ContentRootPath, "Models", "ffmpeg.exe");
        _fontsFolder = Path.Combine(environment.ContentRootPath, "Fonts");
    }

    public async Task RenderKaraokeVideoAsync(string audioPath, string assSubtitlesPath, string outputVideoPath)
    {
        if (!File.Exists(audioPath)) throw new FileNotFoundException("Исходный аудиофайл не найден.");
        if (!File.Exists(assSubtitlesPath)) throw new FileNotFoundException("Файл субтитров не найден.");
        if (!File.Exists(_ffmpegPath)) throw new FileNotFoundException($"FFmpeg не найден по пути: {_ffmpegPath}");

        // Экранируем пути для FFmpeg (особенно важно для Windows, где обратные слэши)
        // Для фильтра subtitles в FFmpeg путь должен быть с прямыми слэшами и экранированными двоеточиями
        string escapedAssPath = assSubtitlesPath.Replace("\\", "/").Replace(":", "\\:");

        // КОМАНДА ДЛЯ ЭФФЕКТА APPLE MUSIC:
        // 1. Генерируем динамический поток testsrc2
        // 2. Размываем его (boxblur), превращая движение в жидкий градиент
        // 3. Накладываем виньетку (vignette), чтобы слегка притемнить края экрана для фокуса на тексте
        // 4. Впекаем отцентрированные субтитры поверх готового фона
        // Генерируем случайный сдвиг по цветовому кругу от 0 до 360 градусов при каждом запуске
        // 1. Узнаем точную длительность аудио с помощью NAudio, чтобы ограничить генератор фона
        double audioDurationSeconds;
        using (var reader = new NAudio.Wave.AudioFileReader(audioPath))
        {
            audioDurationSeconds = reader.TotalTime.TotalSeconds;
        }

        string durationStr = audioDurationSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        // Генерируем случайный базовый тон от 0 до 360 градусов (Hue по цветовому колесу)
        int randomHue = Random.Shared.Next(0, 360);

        // Рандомим небольшое смещение скорости перелива для уникальности каждого трека
        double speedModifier = Random.Shared.NextDouble() * 0.4 + 0.2; // от 0.2 до 0.6
        string speedStr = speedModifier.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        // Формируем фильтр-комплекс
        // 1. geq генерирует плавные ч/б волны, используя только канал яркости (lum). Никаких двоеточий!
        // 2. colorize окрашивает эти волны в один тон (hue), выставляя отличную насыщенность (saturation=0.7)
        //    и среднюю яркость (lightness=0.5), чтобы не было черных или засвеченных мест.
        string arguments = $"-f lavfi -t {durationStr} -i \"color=c=black:s=540x960:r=30\" " +
                   $"-i \"{audioPath}\" " +
                   $"-filter_complex \"" +
                   $"[0:v]geq=lum='128+110*sin(X/W+T*{speedStr})*cos(Y/H+T*{speedStr})'[bw_grid];" +

                   // ИСПРАВЛЕНО: Заменили colorize на hue
                   $"[bw_grid]hue=h={randomHue}:s=0.7[raw_grad];" +

                   // Апскейлим до 1080x1920
                   $"[raw_grad]scale=1080:1920:flags=bicubic," +

                   // Мощное размытие для превращения волн в жидкий шелк
                   $"boxblur=luma_radius=150:luma_power=4:chroma_radius=150:chroma_power=4," +

                   // ИСПРАВЛЕНО: Обернули путь в filename='...' для однозначного парсинга
                   $"subtitles=filename='{escapedAssPath}'[outv]\" " +

                   $"-map \"[outv]\" -map 1:a " +

                   // Параметры совместимости для открытия на любых старых плеерах Windows/смартфонах
                   $"-c:v libx264 -preset ultrafast -profile:v high -level:v 4.1 -pix_fmt yuv420p -crf 23 -c:a aac -y \"{outputVideoPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Добавляем переменную окружения для этого конкретного процесса
        startInfo.EnvironmentVariables["FONTCONFIG_FILE"] = Path.Combine(_fontsFolder, "fonts.conf");
        // Или просто указываем директорию (в зависимости от сборки FFmpeg):
        startInfo.EnvironmentVariables["FC_CONFIG_DIR"] = _fontsFolder;

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