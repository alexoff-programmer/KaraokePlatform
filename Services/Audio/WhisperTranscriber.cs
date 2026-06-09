using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KaraokePlatform.Settings;
using Microsoft.Extensions.Options;
using NAudio.Dsp;
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

    public WhisperTranscriber(IOptions<WhisperSettings> settings)
    {
        _modelPath = settings.Value.ModelPath;
    }

    /// <summary>
    /// Адаптированный метод для VideoProcessingWorker.
    /// Преобразует аудио, фильтрует частоты, распознает речь, формирует караоке-разметку и сохраняет в .ass файл.
    /// </summary>
    public async Task<string> ProcessAudioAsync(string mp3FilePath, string outputFolder, string language, Action<int> onProgress)
    {
        string wavPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.wav");
        string assFileName = $"{Guid.NewGuid()}.ass";
        string assOutputPath = Path.Combine(outputFolder, assFileName);

        string targetLanguage = string.IsNullOrWhiteSpace(language) ? "ru" : language;

        try
        {
            onProgress.Invoke(5); // Конвертация и аудио-фильтрация начались

            // 1. Конвертация аудио с пополосным караоке-фильтром частот (MP3 -> Bandpass Filter -> WAV 16kHz Mono)
            ConvertAndFilterMp3ToWav(mp3FilePath, wavPath);

            onProgress.Invoke(10); // Обработка аудио завершена

            // 2. Распознавание речи с пословными таймингами
            var words = await TranscribeAndMergeTokensAsync(wavPath, targetLanguage, onProgress);

            // 3. Генерация контента .ASS файла и запись на диск
            string assContent = GenerateAssStyleMarkup(words);
            await File.WriteAllTextAsync(assOutputPath, assContent, Encoding.UTF8);

            onProgress.Invoke(100);
            return assOutputPath;
        }
        finally
        {
            if (File.Exists(wavPath))
            {
                File.Delete(wavPath);
            }
        }
    }

    /// <summary>
    /// Извлекает аудио, понижает дискретизацию до 16кГц, микширует в моно 
    /// и вырезает инструментальные шумы вне диапазона человеческого голоса.
    /// </summary>
    private void ConvertAndFilterMp3ToWav(string inputPath, string outputPath)
    {
        using var reader = new AudioFileReader(inputPath);

        // Ресемплинг аудиопотока в стандартные для Whisper 16000 Гц
        var resampler = new WdlResamplingSampleProvider(reader, 16000);

        // Сведение стерео-каналов в моно
        var monoProvider = resampler.ToMono();

        // ВСТРОЕННЫЙ СПОСОБ: Применяем полосовой фильтр (Bandpass Filter). 
        // Центральная частота голоса ~2100 Гц. Добротность (Q) = 0.4f дает плавный срез, 
        // эффективно изолируя полосу частот (~200 Гц - ~4000 Гц). Басы и электроника затихают.
        var filteredProvider = new BiQuadFilterSampleProvider(
            monoProvider,
            BiQuadFilterLocal.CreateBandPassFilter(16000, 2100, 0.4f)
        );

        // Записываем финальный очищенный "вокальный" WAV-файл для нейросети
        WaveFileWriter.CreateWaveFile16(outputPath, filteredProvider);
    }

    private async Task<List<WordTimeInfo>> TranscribeAndMergeTokensAsync(string wavPath, string language, Action<int> onProgress)
    {
        var allWords = new List<WordTimeInfo>();

        using var factory = WhisperFactory.FromPath(_modelPath);
        using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithTokenTimestamps()
            .WithTemperature(0.0f)     // Убираем случайность генерации текста
            .WithProgressHandler(progress =>
            {
                int scaledProgress = 10 + (progress * 40 / 100);
                onProgress.Invoke(scaledProgress);
            })
            .Build();

        using var fileStream = File.OpenRead(wavPath);

        await foreach (var segment in processor.ProcessAsync(fileStream))
        {
            // Если Whisper считает, что это фоновый шум без речи — пропускаем этот сегмент
            if (segment.NoSpeechProbability > 0.5) continue;

            var cleanText = segment.Text.Trim();

            // Фильтруем классические текстовые галлюцинации Whisper на музыке
            if (string.IsNullOrWhiteSpace(cleanText) ||
                cleanText.Contains("Редактор субтитров") ||
                cleanText.Contains("Субтитры созданы") ||
                cleanText.Contains("Корректор"))
            {
                continue;
            }

            var expectedWords = cleanText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (expectedWords.Length == 0) continue;

            var segmentWords = new List<WordTimeInfo>();
            var tokens = segment.Tokens;
            int tokenIndex = 0;

            string activeTokenText = "";
            TimeSpan? activeTokenStart = null;
            TimeSpan? activeTokenEnd = null;

            foreach (var word in expectedWords)
            {
                var wordInfo = new WordTimeInfo { Text = word };
                string cleanTargetWord = string.Concat(word.Where(char.IsLetterOrDigit)).ToLower();

                if (string.IsNullOrEmpty(cleanTargetWord))
                {
                    TimeSpan fallbackTime = tokenIndex < tokens.Length
                        ? TimeSpan.FromMilliseconds(tokens[tokenIndex].Start * 10)
                        : (segmentWords.Count > 0 ? segmentWords.Last().End : segment.Start);

                    wordInfo.Start = fallbackTime;
                    wordInfo.End = fallbackTime + TimeSpan.FromMilliseconds(50);
                    segmentWords.Add(wordInfo);
                    continue;
                }

                var currentWordProgress = new StringBuilder();
                TimeSpan? wordStart = null;
                TimeSpan? wordEnd = null;

                while (currentWordProgress.Length < cleanTargetWord.Length)
                {
                    if (string.IsNullOrEmpty(activeTokenText))
                    {
                        if (tokenIndex >= tokens.Length) break;

                        var token = tokens[tokenIndex];
                        tokenIndex++;

                        var tText = token.Text?.Replace(" ", "") ?? "";
                        bool isServiceToken = (tText.StartsWith("[") && tText.EndsWith("]")) ||
                                              (tText.StartsWith("<") && tText.EndsWith(">"));

                        if (string.IsNullOrEmpty(tText) || isServiceToken) continue;

                        activeTokenText = string.Concat(tText.Where(char.IsLetterOrDigit)).ToLower();
                        activeTokenStart = TimeSpan.FromMilliseconds(token.Start * 10);
                        activeTokenEnd = TimeSpan.FromMilliseconds(token.End * 10);

                        if (string.IsNullOrEmpty(activeTokenText))
                        {
                            if (wordStart != null) wordEnd = activeTokenEnd;
                            continue;
                        }
                    }

                    if (wordStart == null) wordStart = activeTokenStart;
                    wordEnd = activeTokenEnd;

                    int neededLength = cleanTargetWord.Length - currentWordProgress.Length;

                    if (activeTokenText.Length <= neededLength)
                    {
                        currentWordProgress.Append(activeTokenText);
                        activeTokenText = "";
                    }
                    else
                    {
                        string part = activeTokenText.Substring(0, neededLength);
                        currentWordProgress.Append(part);
                        activeTokenText = activeTokenText.Substring(neededLength);
                    }
                }

                wordInfo.Start = wordStart ?? segment.Start;
                wordInfo.End = wordEnd ?? segment.End;

                segmentWords.Add(wordInfo);
            }

            // ЛОКАЛЬНЫЙ РЕМОНТ ТАЙМИНГОВ СЛОВ
            for (int i = 0; i < segmentWords.Count; i++)
            {
                var currentWord = segmentWords[i];
                double wordDurationMs = (currentWord.End - currentWord.Start).TotalMilliseconds;

                // Защита от нулевой длины и аномального растягивания слова Whisper-ом на музыке
                if (currentWord.End <= currentWord.Start || wordDurationMs > 1200)
                {
                    TimeSpan prevEnd = (i > 0) ? segmentWords[i - 1].End : segment.Start;
                    currentWord.Start = prevEnd;

                    int expectedMs = Math.Clamp(currentWord.Text.Length * 90, 180, 500);
                    currentWord.End = currentWord.Start + TimeSpan.FromMilliseconds(expectedMs);
                }

                // Коррекция наложений (Anti-overlap внутри сегмента)
                if (i > 0 && currentWord.Start < segmentWords[i - 1].End)
                {
                    if (segmentWords[i - 1].End < currentWord.End)
                    {
                        currentWord.Start = segmentWords[i - 1].End;
                    }
                    else
                    {
                        var midPoint = TimeSpan.FromMilliseconds((currentWord.Start.TotalMilliseconds + currentWord.End.TotalMilliseconds) / 2);
                        segmentWords[i - 1].End = midPoint;
                        currentWord.Start = midPoint;
                    }
                }

                // Привязка к границам физического сегмента
                if (currentWord.End > segment.End)
                {
                    currentWord.End = segment.End;
                    if (currentWord.Start > currentWord.End)
                    {
                        currentWord.Start = currentWord.End - TimeSpan.FromMilliseconds(100);
                    }
                }
            }

            allWords.AddRange(segmentWords);
        }

        return allWords;
    }

    private string GenerateAssStyleMarkup(List<WordTimeInfo> words)
    {
        if (words == null || words.Count == 0) return string.Empty;

        var sb = new StringBuilder();

        // Заголовки конфигурации ASS (Оптимизировано под Shorts/Reels 1080x1920)
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine("PlayResX: 1080");
        sb.AppendLine("PlayResY: 1920");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine("Style: KaraokeStyle,Montserrat,90,&H00FFFFFF,&H0000FFFF,&H00000000,&H00000000,-1,0,0,0,100,100,2,0,1,2,0,5,100,100,0,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        var timeShift = TimeSpan.FromMilliseconds(200); // Компенсация задержки восприятия глаза

        // 1. Группируем слова в естественные музыкальные фразы
        var phrases = new List<List<WordTimeInfo>>();
        var currentPhrase = new List<WordTimeInfo> { words[0] };

        for (int i = 1; i < words.Count; i++)
        {
            var prevWord = words[i - 1];
            var currWord = words[i];

            double gapMs = (currWord.Start - prevEndSafe(prevWord)).TotalMilliseconds;

            // Если пауза между словами больше 280 мс или в строке уже набралось 4 слова — закрываем фразу
            if (gapMs > 280 || currentPhrase.Count >= 4)
            {
                phrases.Add(currentPhrase);
                currentPhrase = new List<WordTimeInfo>();
            }
            currentPhrase.Add(currWord);
        }
        if (currentPhrase.Count > 0) phrases.Add(currentPhrase);

        // 2. Генерируем блоки субтитров с "бесшовным" удержанием текста на экране
        for (int i = 0; i < phrases.Count; i++)
        {
            var phrase = phrases[i];

            var lineStart = phrase.First().Start - timeShift;
            if (lineStart < TimeSpan.Zero) lineStart = TimeSpan.Zero;

            // ЭФФЕКТ УДЕРЖАНИЯ СУБТИТРОВ:
            // Если есть следующая фраза, то текущая строка горит на экране ровно до тех пор,
            // пока не начнется СЛЕДУЮЩАЯ фраза. Никаких пустых промежутков!
            TimeSpan lineEnd;
            if (i < phrases.Count - 1)
            {
                var nextPhraseStart = phrases[i + 1].First().Start - timeShift;

                // Защитный лимит: если между фразами гигантский инструментал (больше 4 секунд),
                // то держать текст глупо, лучше его погасить через 1.5 секунды после окончания слов.
                double cleanGap = (nextPhraseStart - (phrase.Last().End - timeShift)).TotalMilliseconds;
                if (cleanGap > 4000)
                {
                    lineEnd = phrase.Last().End - timeShift + TimeSpan.FromMilliseconds(1500);
                }
                else
                {
                    lineEnd = nextPhraseStart;
                }
            }
            else
            {
                // Для самой последней фразы в песне оставляем обычный хвост + 1 секунда, чтобы текст не исчезал на полуслове
                lineEnd = phrase.Last().End - timeShift + TimeSpan.FromMilliseconds(1000);
            }

            if (lineEnd < lineStart) lineEnd = lineStart + TimeSpan.FromMilliseconds(500);

            string assStart = FormatTimeSpanForAss(lineStart);
            string assEnd = FormatTimeSpanForAss(lineEnd);

            var lineBuilder = new StringBuilder();

            // ВСТРОЕННЫЙ ЭФФЕКТ АНИМАЦИИ:
            // Указывает плееру плавно проявить блок за 200мс в начале и затушить за 200мс в самом конце его существования.
            lineBuilder.Append("{\\fad(200, 200)}");

            TimeSpan currentTime = lineStart;

            for (int j = 0; j < phrase.Count; j++)
            {
                var word = phrase[j];

                var shiftedWordStart = word.Start - timeShift;
                var shiftedWordEnd = word.End - timeShift;

                if (shiftedWordStart < TimeSpan.Zero) shiftedWordStart = TimeSpan.Zero;
                if (shiftedWordEnd < shiftedWordStart) shiftedWordEnd = shiftedWordStart;

                // Корректировка наложений во времени внутри блока
                if (shiftedWordStart < currentTime) shiftedWordStart = currentTime;
                if (shiftedWordEnd < shiftedWordStart) shiftedWordEnd = shiftedWordStart;

                // Расчет паузы перед словом внутри фразы
                var pauseDuration = shiftedWordStart - currentTime;
                if (pauseDuration.TotalMilliseconds > 10)
                {
                    int pauseCs = (int)(pauseDuration.TotalMilliseconds / 10);
                    lineBuilder.Append($"{{\\kf{pauseCs}}}");
                }

                // Расчет длительности караоке-подсветки слова (в сантисекундах)
                int wordCs = (int)Math.Round((shiftedWordEnd - shiftedWordStart).TotalMilliseconds / 10.0);
                if (wordCs <= 0) wordCs = 1;

                string trailingSpace = (j != phrase.Count - 1) ? " " : "";
                lineBuilder.Append($"{{\\kf{wordCs}}}{word.Text}{trailingSpace}");

                currentTime = shiftedWordEnd;
            }

            // ВАЖНЫЙ ХАК ДЛЯ ASS КАРАОКЕ:
            // Так как мы искусственно продлили жизнь строки субтитра (lineEnd теперь равен старту следующей строки),
            // нам нужно закрасить "остаток времени пассивного ожидания" в самом конце этой строки.
            // Если этого не сделать, плеер (например FFmpeg или VLC) растянет подсветку последнего слова до самого конца кадра.
            if (currentTime < lineEnd)
            {
                var finalWaitDuration = lineEnd - currentTime;
                int finalWaitCs = (int)(finalWaitDuration.TotalMilliseconds / 10);
                if (finalWaitCs > 0)
                {
                    lineBuilder.Append($"{{\\kf{finalWaitCs}}}");
                }
            }

            sb.AppendLine($"Dialogue: 0,{assStart},{assEnd},KaraokeStyle,,0,0,0,,{lineBuilder}");
        }

        return sb.ToString();

        TimeSpan prevEndSafe(WordTimeInfo w) => w.End > w.Start ? w.End : w.Start + TimeSpan.FromMilliseconds(200);
    }

    private string FormatTimeSpanForAss(TimeSpan ts)
    {
        int centiseconds = ts.Milliseconds / 10;
        return $"{ts.Hours:D1}:{ts.Minutes:D2}:{ts.Seconds:D2}.{centiseconds:D2}";
    }
}

/// <summary>
/// Реализация аудио-провайдера фильтрации NAudio для работы в C# конвейере.
/// </summary>
public class BiQuadFilterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly BiQuadFilter _filter;

    public BiQuadFilterSampleProvider(ISampleProvider source, BiQuadFilter filter)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        for (int n = 0; n < samplesRead; n++)
        {
            buffer[offset + n] = _filter.Transform(buffer[offset + n]);
        }
        return samplesRead;
    }
}

public static class BiQuadFilterLocal
{
    public static BiQuadFilter CreateBandPassFilter(int sampleRate, float centerFrequency, float q)
    {
        return BiQuadFilter.BandPassFilterConstantSkirtGain(sampleRate, centerFrequency, q);
    }
}