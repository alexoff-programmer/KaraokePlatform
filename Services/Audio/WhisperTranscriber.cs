using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KaraokePlatform.Settings;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace KaraokePlatform.Services.Audio;

public record WordTimeInfo
{
    public string Text { get; set; } = string.Empty;
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public TimeSpan Duration => End - Start;
}

public class WhisperTranscriber
{
    private readonly string _modelPath;

    // Предполагаем, что путь к модели инжектится через конфигурацию или жестко задан
    public WhisperTranscriber(IOptions<WhisperSettings> settings)
    {
        _modelPath = settings.Value.ModelPath;
    }

    /// <summary>
    /// Адаптированный метод для VideoProcessingWorker.
    /// Преобразует аудио, распознает речь, формирует караоке-разметку и сохраняет в .ass файл.
    /// </summary>
    /// <param name="mp3FilePath">Путь к исходному аудиофайлу.</param>
    /// <param name="outputFolder">Папка для сохранения результата.</param>
    /// <param name="language">Язык распознавания (например, "ru", "en").</param>
    /// <returns>Абсолютный путь к созданному .ass файлу субтитров.</returns>
    public async Task<string> ProcessAudioAsync(string mp3FilePath, string outputFolder, string language, Action<int> onProgress)
    {
        string wavPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.wav");
        string assFileName = $"{Guid.NewGuid()}.ass";
        string assOutputPath = Path.Combine(outputFolder, assFileName);

        // Защита по языку: если пустой или null, ставим русский по умолчанию
        string targetLanguage = string.IsNullOrWhiteSpace(language) ? "ru" : language;

        try
        {
            onProgress.Invoke(5); // Конвертация началась
            // 1. Конвертация аудио (MP3 -> WAV 16kHz Mono)
            ConvertMp3ToWav(mp3FilePath, wavPath);
            onProgress.Invoke(10); // Конвертация завершена

            // 2. Распознавание речи с пословными таймингами
            var words = await TranscribeAndMergeTokensAsync(wavPath, targetLanguage, onProgress);
            // 3. Генерация контента .ASS файла и запись на диск
            string assContent = GenerateAssStyleMarkup(words);
            await File.WriteAllTextAsync(assOutputPath, assContent, Encoding.UTF8);

            onProgress.Invoke(50);
            return assOutputPath;
        }
        finally
        {
            // Чистим временный WAV-файл в любых непонятных ситуациях
            if (File.Exists(wavPath))
            {
                File.Delete(wavPath);
            }
        }
    }

    private void ConvertMp3ToWav(string inputPath, string outputPath)
    {
        using var reader = new AudioFileReader(inputPath);
        var resampler = new WdlResamplingSampleProvider(reader, 16000);
        var monoProvider = resampler.ToMono();
        WaveFileWriter.CreateWaveFile16(outputPath, monoProvider);
    }

    private async Task<List<WordTimeInfo>> TranscribeAndMergeTokensAsync(string wavPath, string language, Action<int> onProgress)
    {
        var allWords = new List<WordTimeInfo>();

        using var factory = WhisperFactory.FromPath(_modelPath);
        using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithTokenTimestamps()
            .WithProgressHandler(progress =>
            {
                // progress — это int от 0 до 100 от whisper.cpp.
                // Масштабируем его, чтобы этап распознавания занимал, например, от 10% до 50% общего прогресса
                int scaledProgress = 10 + (progress * 40 / 100);

                // Просто дергаем callback. Никакого SignalR здесь!
                onProgress.Invoke(scaledProgress);
            })
            .Build();

        using var fileStream = File.OpenRead(wavPath);

        await foreach (var segment in processor.ProcessAsync(fileStream))
        {
            var cleanText = segment.Text.Trim();
            var expectedWords = cleanText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (expectedWords.Length == 0) continue;

            var segmentWords = new List<WordTimeInfo>();
            var tokens = segment.Tokens;
            int tokenIndex = 0;

            foreach (var word in expectedWords)
            {
                var wordInfo = new WordTimeInfo { Text = word };
                var currentTokenString = new StringBuilder();
                TimeSpan? wordStart = null;
                TimeSpan? wordEnd = null;

                while (tokenIndex < tokens.Length && currentTokenString.Length < word.Length)
                {
                    var token = tokens[tokenIndex];
                    var tokenText = token.Text?.Replace(" ", "") ?? "";

                    // Фильтруем пустые токены, а также служебные токены Whisper (например, [_BEG_], [_END_], <|startoftranscript|>)
                    bool isServiceToken = tokenText.StartsWith("[") && tokenText.EndsWith("]") ||
                                          tokenText.StartsWith("<") && tokenText.EndsWith(">");

                    if (string.IsNullOrEmpty(tokenText) || isServiceToken)
                    {
                        tokenIndex++;
                        continue;
                    }

                    // ОШИБКА ИСПРАВЛЕНА: явно приводим long (миллисекунды) к TimeSpan
                    if (wordStart == null) wordStart = TimeSpan.FromMilliseconds(token.Start * 10);
                    wordEnd = TimeSpan.FromMilliseconds(token.End * 10);

                    currentTokenString.Append(tokenText);
                    tokenIndex++;
                }

                wordInfo.Start = wordStart ?? segment.Start;
                wordInfo.End = wordEnd ?? segment.End;
                segmentWords.Add(wordInfo);
            }

            // Интеллектуальная страховка на случай сбоя таймингов
            bool isTimestampsBroken = segmentWords.Any(w => w.Duration.TotalMilliseconds <= 0) ||
                                      segmentWords.Last().End > segment.End;

            if (isTimestampsBroken)
            {
                var segmentDuration = segment.End - segment.Start;
                var timePerWord = TimeSpan.FromMilliseconds(segmentDuration.TotalMilliseconds / expectedWords.Length);

                for (int i = 0; i < segmentWords.Count; i++)
                {
                    segmentWords[i].Start = segment.Start + (timePerWord * i);
                    segmentWords[i].End = segmentWords[i].Start + timePerWord;
                }
            }

            allWords.AddRange(segmentWords);
        }

        return allWords;
    }

    /// <summary>
    /// Генерирует валидную структуру .ASS файла с караоке-эффектом закрашивания.
    /// </summary>
    /// <summary>
    /// Генерирует структуру .ASS файла с караоке-эффектом закрашивания.
    /// Группирует больше слов за раз и разбивает их на 2 строки для лучшей читаемости.
    /// </summary>
    private string GenerateAssStyleMarkup(List<WordTimeInfo> words)
    {
        var sb = new StringBuilder();

        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine("PlayResX: 1080");
        sb.AppendLine("PlayResY: 1920");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");

        // Параметры цвета в формате ASS (&HAABBGGRR - Alpha, Blue, Green, Red):
        // PrimaryColour (Активное слово):   &H00FFFFFF -> Чистый белый, 100% непрозрачность
        // SecondaryColour (Ожидание):       &HB0EFEFEF -> Почти белый, но с ~70% прозрачностью (дает эффект приглушенности)
        // OutlineColour (Контур):           &H00000000 -> Черный, но толщина Outline=0, он не виден, служит хаком для рендереров
        // BackColour (Тень):                &H90000000 -> Черная тень с мягкой ~55% прозрачностью для объема на светлых участках градиента

        // Шрифт: С интервалом между букв (Spacing: 1) и увеличенным масштабом для веса.
        // Alignment: 5 (Строгий центр). MarginV: 0 (так как выравнивание по центру, MarginV сдвигает текст от центра вверх/вниз)
        sb.AppendLine("Style: KaraokeStyle,Arial Black,72,&H00FFFFFF,&HB0EFEFEF,&H00000000,&H90000000,-1,0,0,0,105,100,1,0,1,0,8,5,80,80,0,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        // НАСТРОЙКА: Сколько слов объединять в один блок субтитров
        const int wordsPerBlock = 6;

        for (int i = 0; i < words.Count; i += wordsPerBlock)
        {
            var chunk = words.Skip(i).Take(wordsPerBlock).ToList();

            // Время жизни всего блока на экране
            var lineStart = chunk.First().Start;
            var lineEnd = chunk.Last().End;

            string assStart = FormatTimeSpanForAss(lineStart);
            string assEnd = FormatTimeSpanForAss(lineEnd);

            var lineBuilder = new StringBuilder();
            TimeSpan currentTime = lineStart;

            // Определяем индекс, на котором сделаем перенос строки (ровно посередине чанка)
            int halfIndex = chunk.Count / 2;

            for (int j = 0; j < chunk.Count; j++)
            {
                var word = chunk[j];

                // Вставляем перенос строки \N перед элементом второй половины
                if (j == halfIndex)
                {
                    // Убираем лишний пробел перед переносом, если он остался от предыдущего слова
                    if (lineBuilder.Length > 0 && lineBuilder[lineBuilder.Length - 1] == ' ')
                    {
                        lineBuilder.Length--;
                    }
                    lineBuilder.Append("\\N");
                }

                // Считаем паузу перед словом
                var pauseDuration = word.Start - currentTime;
                if (pauseDuration.TotalMilliseconds > 0)
                {
                    int pauseCs = (int)Math.Round(pauseDuration.TotalMilliseconds / 10.0);
                    lineBuilder.Append($"{{\\kf{pauseCs}}} ");
                }

                // Считаем длительность подсветки слова
                int wordCs = (int)Math.Round(word.Duration.TotalMilliseconds / 10.0);

                // Добавляем караоке тег и само слово
                lineBuilder.Append($"{{\\kf{wordCs}}}{word.Text} ");

                currentTime = word.End;
            }

            // Записываем готовую двухстрочную реплику в ASS файл
            sb.AppendLine($"Dialogue: 0,{assStart},{assEnd},KaraokeStyle,,0,0,0,,{lineBuilder.ToString().Trim()}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Хелпер форматирования TimeSpan в стандарт субтитров ASS (H:MM:SS.cs)
    /// </summary>
    private string FormatTimeSpanForAss(TimeSpan ts)
    {
        // ASS требует формат с двумя знаками после запятой (сантисекунды)
        int centiseconds = ts.Milliseconds / 10;
        return $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}.{centiseconds:D2}";
    }
}