using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KaraokePlatform.Services.Audio.Interfaces;
using KaraokePlatform.Services.Audio.Records;

namespace KaraokePlatform.Services.Audio;

public class AssSubtitleGenerator : ISubtitleGenerator
{
    private static readonly TimeSpan TimeShift = TimeSpan.Zero;
    private const double InstrumentSilenceThresholdMs = 1200;

    private int FadeTimeMs = 150;
    private int FadeBufferMs = 50;

    private static readonly TimeSpan LeadInTime = TimeSpan.FromSeconds(1.5);

    private static readonly Regex PunctuationCleanRegex = new Regex(@"(^[\p{P}&&[^\-']]+)|([\p{P}&&[^\-']]+$)", RegexOptions.Compiled);

    private static string MapFontName(string? uiFont) => "Arial";

    // Вспомогательный метод для перевода веб-цветов (#RRGGBB или названия) в формат ASS (AABBGGRR)
    private string ConvertToAssColor(string? uiColor, string defaultAssColor)
    {
        if (string.IsNullOrWhiteSpace(uiColor)) return defaultAssColor;

        uiColor = uiColor.Trim().ToLower();

        // Если пришел HEX из ColorPicker (#RRGGBB или RRGGBB)
        if (uiColor.StartsWith("#")) uiColor = uiColor.Substring(1);

        if (uiColor.Length == 6 && Regex.IsMatch(uiColor, @"^[0-9a-f]{6}$"))
        {
            string r = uiColor.Substring(0, 2);
            string g = uiColor.Substring(2, 2);
            string b = uiColor.Substring(4, 2);
            // Формат ASS требует инвертированный порядок: Синий, Зеленый, Красный (BBGGRR)
            return $"&H00{b}{g}{r}";
        }

        // Именованные фолбеки, если из UI прилетели строки
        return uiColor switch
        {
            "lightgray" => "&H00D3D3D3",
            "cyan" => "&H00FFFF00",
            "yellow" => "&H0000FFFF",
            "red" => "&H000000FF",
            "green" => "&H0000FF00",
            _ => defaultAssColor
        };
    }

    public string GenerateKaraokeMarkup(List<WordTimeInfo> words, string? fontName = null, string? fillStyle = null, string? primaryColor = null, string? secondaryColor = null, string? videoFormat = null)
    {
        if (words == null || words.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        BuildAssHeader(sb, fontName, fillStyle, primaryColor, secondaryColor, videoFormat);

        var phrases = GroupWordsIntoPhrases(words);
        ExtendPhraseEnds(phrases);
        BuildDialogueLines(sb, phrases, fillStyle);

        return sb.ToString();
    }

    public string GenerateKaraokeMarkupFromPhrases(List<List<WordTimeInfo>> phrases, string? fontName = null, string? fillStyle = null, string? primaryColor = null, string? secondaryColor = null, string? videoFormat = null)
    {
        if (phrases == null || phrases.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        BuildAssHeader(sb, fontName, fillStyle, primaryColor, secondaryColor, videoFormat);

        // Никакой повторной интерполяции! Передаем проверенные данные напрямую
        BuildDialogueLinesFromPhrases(sb, phrases, fillStyle);

        return sb.ToString();
    }

    private void BuildAssHeader(StringBuilder sb, string? fontName = null, string? fillStyle = null, string? primaryColor = null, string? secondaryColor = null, string? videoFormat = null)
    {
        var font = MapFontName(fontName);
        bool isLandscape = videoFormat == "landscape";
        int resX = isLandscape ? 1920 : 1080;
        int resY = isLandscape ? 1080 : 1920;
        int fontSize = isLandscape ? 52 : 68;

        // Корректно парсим цвета из UI
        var activeColor = ConvertToAssColor(secondaryColor, "&H00F04CFF"); // Цвета закраски активного слога
        var inactiveColor = ConvertToAssColor(primaryColor, "&H00FFFFFF");  // Цвет ожидающего текста

        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {resX}");
        sb.AppendLine($"PlayResY: {resY}");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");

        // Тень и обводка выставляются в темный цвет для читаемости на любом фоне
        sb.AppendLine($"Style: KaraokeStyle,{font},{fontSize},{activeColor},{inactiveColor},&H001A1A1A,&H00000000,-1,0,0,0,100,100,2,0,1,3,1,5,100,100,0,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
    }

    // Добавляем параметр fillStyle в логику сборки строк диалогов
    private void BuildDialogueLinesFromPhrases(StringBuilder sb, List<List<WordTimeInfo>> phrases, string? fillStyle)
    {
        TimeSpan absoluteMinStart = TimeSpan.Zero;

        for (int i = 0; i < phrases.Count; i++)
        {
            var phrase = phrases[i];

            var phraseStart = phrase.First().Start - TimeShift;
            var phraseEnd = phrase.Last().End - TimeShift;

            if (phraseStart < TimeSpan.Zero) phraseStart = TimeSpan.Zero;
            if (phraseEnd < phraseStart) phraseEnd = phraseStart;

            var lineStart = phraseStart - LeadInTime;
            if (lineStart < TimeSpan.Zero) lineStart = TimeSpan.Zero;

            if (lineStart < absoluteMinStart) lineStart = absoluteMinStart;
            if (lineStart > phraseStart) lineStart = phraseStart;

            var lineEnd = phraseEnd;
            if (lineEnd < lineStart) lineEnd = lineStart + TimeSpan.FromMilliseconds(500);

            absoluteMinStart = lineEnd + TimeSpan.FromMilliseconds(FadeBufferMs);

            string assStart = FormatTimeSpanForAss(lineStart);
            string assEnd = FormatTimeSpanForAss(lineEnd);

            var lineBuilder = new StringBuilder($"{{\\fad({FadeTimeMs}, {FadeTimeMs})}}");

            // Инжекция градиента libass, если выбран режим "gradient"
            if (fillStyle?.ToLower() == "gradient")
            {
                // \1vc — вертикальный градиент основного текста, \2vc — градиент закраски караоке
                // Изменяем тональность сверху вниз (к примеру, подмешивая небольшое затемнение к базовым цветам)
                lineBuilder.Append(@"{\1vc(FFFFFF,FFFFFF,D0D0D0,D0D0D0)}");
            }

            int delayCs = (int)Math.Round((phraseStart - lineStart).TotalMilliseconds / 10.0);
            if (delayCs > 0) lineBuilder.Append($"{{\\k{delayCs}}}");

            TimeSpan currentTime = phraseStart;

            for (int j = 0; j < phrase.Count; j++)
            {
                var word = phrase[j];
                var shiftedWordStart = word.Start - TimeShift;
                var shiftedWordEnd = word.End - TimeShift;

                if (shiftedWordStart < TimeSpan.Zero) shiftedWordStart = TimeSpan.Zero;
                if (shiftedWordEnd < shiftedWordStart) shiftedWordEnd = shiftedWordStart;

                // Мягкое ограничение старта без изменения длительности слова!
                if (shiftedWordStart < currentTime)
                {
                    shiftedWordStart = currentTime;
                    if (shiftedWordEnd < shiftedWordStart) 
                        shiftedWordEnd = shiftedWordStart.Add(TimeSpan.FromMilliseconds(50));
                }

                var pauseDuration = shiftedWordStart - currentTime;
                if (pauseDuration.TotalMilliseconds > 10)
                {
                    int pauseCs = (int)Math.Round(pauseDuration.TotalMilliseconds / 10.0);
                    if (pauseCs > 0) lineBuilder.Append($"{{\\kf{pauseCs}}}");
                }

                int wordCs = (int)Math.Round((shiftedWordEnd - shiftedWordStart).TotalMilliseconds / 10.0);
                if (wordCs <= 0) wordCs = 1;

                string trailingSpace = (j != phrase.Count - 1) ? " " : "";
                AppendCharacterLevelKaraoke(lineBuilder, word.Text, wordCs);
                lineBuilder.Append(trailingSpace);

                currentTime = shiftedWordEnd;
            }

            if (currentTime < lineEnd)
            {
                int finalWaitCs = (int)Math.Round((lineEnd - currentTime).TotalMilliseconds / 10.0);
                if (finalWaitCs > 0) lineBuilder.Append($"{{\\kf{finalWaitCs}}}");
            }

            sb.AppendLine($"Dialogue: 0,{assStart},{assEnd},KaraokeStyle,,0,0,0,,{lineBuilder}");
        }
    }

    private void BuildDialogueLines(StringBuilder sb, List<List<WordTimeInfo>> phrases, string? fillStyle)
    {
        TimeSpan absoluteMinStart = TimeSpan.Zero;

        for (int i = 0; i < phrases.Count; i++)
        {
            var phrase = phrases[i];

            var phraseStart = phrase.First().Start - TimeShift;
            var phraseEnd = phrase.Last().End - TimeShift;

            if (phraseStart < TimeSpan.Zero) phraseStart = TimeSpan.Zero;
            if (phraseEnd < phraseStart) phraseEnd = phraseStart;

            var lineStart = phraseStart - LeadInTime;
            if (lineStart < TimeSpan.Zero) lineStart = TimeSpan.Zero;

            if (lineStart < absoluteMinStart) lineStart = absoluteMinStart;
            if (lineStart > phraseStart) lineStart = phraseStart;

            var lineEnd = phraseEnd;
            if (lineEnd < lineStart) lineEnd = lineStart + TimeSpan.FromMilliseconds(500);

            absoluteMinStart = lineEnd + TimeSpan.FromMilliseconds(FadeBufferMs);

            string assStart = FormatTimeSpanForAss(lineStart);
            string assEnd = FormatTimeSpanForAss(lineEnd);

            var lineBuilder = new StringBuilder($"{{\\fad({FadeTimeMs}, {FadeTimeMs})}}");

            if (fillStyle?.ToLower() == "gradient")
            {
                lineBuilder.Append(@"{\1vc(FFFFFF,FFFFFF,C8C8C8,C8C8C8)}");
            }

            int delayCs = (int)Math.Round((phraseStart - lineStart).TotalMilliseconds / 10.0);
            if (delayCs > 0) lineBuilder.Append($"{{\\k{delayCs}}}");

            TimeSpan currentTime = phraseStart;
            int splitIdx = GetBestSplitIndex(phrase, 40);

            for (int j = 0; j < phrase.Count; j++)
            {
                var word = phrase[j];
                var shiftedWordStart = word.Start - TimeShift;
                var shiftedWordEnd = word.End - TimeShift;

                if (shiftedWordStart < TimeSpan.Zero) shiftedWordStart = TimeSpan.Zero;
                if (shiftedWordEnd < shiftedWordStart) shiftedWordEnd = shiftedWordStart;

                // Мягкое ограничение старта без изменения длительности слова!
                if (shiftedWordStart < currentTime)
                {
                    shiftedWordStart = currentTime;
                    if (shiftedWordEnd < shiftedWordStart) 
                        shiftedWordEnd = shiftedWordStart.Add(TimeSpan.FromMilliseconds(50));
                }

                var pauseDuration = shiftedWordStart - currentTime;
                if (pauseDuration.TotalMilliseconds > 10)
                {
                    int pauseCs = (int)Math.Round(pauseDuration.TotalMilliseconds / 10.0);
                    if (pauseCs > 0) lineBuilder.Append($"{{\\kf{pauseCs}}}");
                }

                int wordCs = (int)Math.Round((shiftedWordEnd - shiftedWordStart).TotalMilliseconds / 10.0);
                if (wordCs <= 0) wordCs = 1;

                if (j == splitIdx) lineBuilder.Append("\\N");

                string trailingSpace = (j != phrase.Count - 1 && j != splitIdx - 1) ? " " : "";
                AppendCharacterLevelKaraoke(lineBuilder, word.Text, wordCs);
                lineBuilder.Append(trailingSpace);

                currentTime = shiftedWordEnd;
            }

            if (currentTime < lineEnd)
            {
                int finalWaitCs = (int)Math.Round((lineEnd - currentTime).TotalMilliseconds / 10.0);
                if (finalWaitCs > 0) lineBuilder.Append($"{{\\kf{finalWaitCs}}}");
            }

            sb.AppendLine($"Dialogue: 0,{assStart},{assEnd},KaraokeStyle,,0,0,0,,{lineBuilder}");
        }
    }

    private static (string Leading, string Core, string Trailing) SplitPunctuation(string wordText)
    {
        if (string.IsNullOrEmpty(wordText)) return (string.Empty, string.Empty, string.Empty);

        var matchLeading = Regex.Match(wordText, @"^[\p{P}&&[^\-']]+");
        string leading = matchLeading.Success ? matchLeading.Value : string.Empty;

        string remaining = wordText.Substring(leading.Length);
        var matchTrailing = Regex.Match(remaining, @"[\p{P}&&[^\-']]+$");
        string trailing = matchTrailing.Success ? matchTrailing.Value : string.Empty;

        string core = remaining.Substring(0, remaining.Length - trailing.Length);
        return (leading, core, trailing);
    }

    public List<List<WordTimeInfo>> GroupWordsIntoPhrases(List<WordTimeInfo> words)
    {
        var phrases = new List<List<WordTimeInfo>>();
        if (words == null || words.Count == 0) return phrases;

        var currentBlock = new List<WordTimeInfo>();

        for (int i = 0; i < words.Count; i++)
        {
            var currWord = words[i];
            string cleanText = PunctuationCleanRegex.Replace(currWord.Text ?? string.Empty, string.Empty);

            if (string.IsNullOrWhiteSpace(cleanText)) continue;

            var processedWord = currWord with { Text = currWord.Text?.Trim() ?? string.Empty };

            if (currentBlock.Count > 0)
            {
                var prevWord = currentBlock.Last();
                TimeSpan prevEnd = prevWord.End > prevWord.Start ? prevWord.End : prevWord.Start + TimeSpan.FromMilliseconds(200);
                double gapMs = (processedWord.Start - prevEnd).TotalMilliseconds;

                bool isPauseTooLong = gapMs > 1500;
                bool canFit = CanFitInTwoLines(currentBlock, processedWord, 40);

                if (isPauseTooLong || !canFit)
                {
                    CapitalizeFirstWord(currentBlock);
                    phrases.Add(currentBlock);
                    currentBlock = new List<WordTimeInfo>();
                }
            }

            currentBlock.Add(processedWord);
        }

        if (currentBlock.Count > 0)
        {
            CapitalizeFirstWord(currentBlock);
            phrases.Add(currentBlock);
        }

        return phrases;
    }

    private bool CanFitInTwoLines(List<WordTimeInfo> blockWords, WordTimeInfo newWord, int maxLineLen)
    {
        var tempWords = new List<WordTimeInfo>(blockWords) { newWord };
        int totalLen = tempWords.Sum(w => PunctuationCleanRegex.Replace(w.Text, string.Empty).Length) + tempWords.Count - 1;
        if (totalLen <= maxLineLen) return true;

        for (int k = 1; k < tempWords.Count; k++)
        {
            int line1Len = tempWords.Take(k).Sum(w => PunctuationCleanRegex.Replace(w.Text, string.Empty).Length) + k - 1;
            int line2Len = tempWords.Skip(k).Sum(w => PunctuationCleanRegex.Replace(w.Text, string.Empty).Length) + (tempWords.Count - k) - 1;

            if (line1Len <= maxLineLen && line2Len <= maxLineLen) return true;
        }

        return false;
    }

    private int GetBestSplitIndex(List<WordTimeInfo> blockWords, int maxLineLen)
    {
        int totalLen = blockWords.Sum(w => PunctuationCleanRegex.Replace(w.Text, string.Empty).Length) + blockWords.Count - 1;
        if (totalLen <= maxLineLen) return -1;

        int bestK = -1;
        int minDiff = int.MaxValue;

        for (int k = 1; k < blockWords.Count; k++)
        {
            int line1Len = blockWords.Take(k).Sum(w => PunctuationCleanRegex.Replace(w.Text, string.Empty).Length) + k - 1;
            int line2Len = blockWords.Skip(k).Sum(w => PunctuationCleanRegex.Replace(w.Text, string.Empty).Length) + (blockWords.Count - k) - 1;

            if (line1Len <= maxLineLen && line2Len <= maxLineLen)
            {
                int diff = Math.Abs(line1Len - line2Len);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    bestK = k;
                }
            }
        }

        if (bestK == -1) bestK = blockWords.Count / 2;
        return bestK;
    }

    private void CapitalizeFirstWord(List<WordTimeInfo> phrase)
    {
        if (phrase == null || phrase.Count == 0) return;

        var firstWordInfo = phrase[0];
        string word = firstWordInfo.Text;

        if (!string.IsNullOrEmpty(word))
        {
            var (leading, core, trailing) = SplitPunctuation(word);
            if (!string.IsNullOrEmpty(core))
            {
                string capitalizedCore = char.ToUpper(core[0]) + core[1..];
                phrase[0] = firstWordInfo with { Text = leading + capitalizedCore + trailing };
            }
            else
            {
                string capitalizedWord = char.ToUpper(word[0]) + word[1..];
                phrase[0] = firstWordInfo with { Text = capitalizedWord };
            }
        }
    }

    private double EaseInOutSine(double x) => -(Math.Cos(Math.PI * x) - 1) / 2.0;

    private void AppendCharacterLevelKaraoke(StringBuilder lineBuilder, string wordText, int totalWordCs)
    {
        if (string.IsNullOrEmpty(wordText)) return;

        var (leading, core, trailing) = SplitPunctuation(wordText);

        if (string.IsNullOrEmpty(core))
        {
            lineBuilder.Append($@"{{\kf0}}{wordText}");
            return;
        }

        if (!string.IsNullOrEmpty(leading))
        {
            lineBuilder.Append($@"{{\kf0}}{leading}");
        }

        int len = core.Length;
        var charDurations = new int[len];
        int baseDurationCs = totalWordCs / len;
        int remainder = totalWordCs % len;
        int sumCs = 0;

        for (int i = 0; i < len; i++)
        {
            int durationCs = baseDurationCs + (i < remainder ? 1 : 0);
            if (durationCs <= 0) durationCs = 1;
            charDurations[i] = durationCs;
            sumCs += durationCs;
        }

        int diff = totalWordCs - sumCs;
        if (diff != 0)
        {
            charDurations[0] = Math.Max(1, charDurations[0] + diff);
        }

        for (int i = 0; i < len; i++)
        {
            lineBuilder.Append($"{{\\kf{charDurations[i]}}}{core[i]}");
        }

        if (!string.IsNullOrEmpty(trailing))
        {
            lineBuilder.Append($@"{{\kf0}}{trailing}");
        }
    }

    private void ExtendPhraseEnds(List<List<WordTimeInfo>> phrases)
    {
        for (int i = 0; i < phrases.Count; i++)
        {
            var currentPhraseWords = phrases[i];
            var lastWord = currentPhraseWords.Last();

            TimeSpan maxAllowedEnd = (i < phrases.Count - 1)
                ? phrases[i + 1].First().Start - TimeSpan.FromMilliseconds(FadeBufferMs)
                : lastWord.End + TimeSpan.FromMilliseconds(1200);

            TimeSpan desiredEnd = lastWord.End + TimeSpan.FromMilliseconds(450);
            if (desiredEnd > maxAllowedEnd) desiredEnd = maxAllowedEnd;

            if (desiredEnd > lastWord.Start) lastWord.End = desiredEnd;
        }
    }

    private string FormatTimeSpanForAss(TimeSpan ts)
    {
        long totalCentiseconds = (long)Math.Round(ts.TotalMilliseconds / 10.0);
        long cs = totalCentiseconds % 100;
        long totalSeconds = totalCentiseconds / 100;
        long secs = totalSeconds % 60;
        long totalMinutes = totalSeconds / 60;
        long mins = totalMinutes % 60;
        long hours = totalMinutes / 60;

        return $"{hours:D1}:{mins:D2}:{secs:D2}.{cs:D2}";
    }

}