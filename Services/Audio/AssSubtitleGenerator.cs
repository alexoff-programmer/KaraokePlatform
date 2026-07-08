using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KaraokePlatform.Services.Audio.Interfaces;
using KaraokePlatform.Services.Audio.Records;

namespace KaraokePlatform.Services.Audio;

public class AssSubtitleGenerator : ISubtitleGenerator
{
    private static readonly TimeSpan TimeShift = TimeSpan.FromMilliseconds(200);
    private const double InstrumentSilenceThresholdMs = 1200;

    // Настройки аккуратного последовательного отображения без каши
    private int FadeTimeMs = 0;
    private int FadeBufferMs = 50; // Небольшой зазор между физическим исчезновением одной строки и появлением другой
    private int MaxHoldAfterSpeechMs = 500;

    // На сколько секунд раньше строка должна появиться на экране для подготовки
    private static readonly TimeSpan PreRollTime = TimeSpan.FromSeconds(2.0);

    // Регулярное выражение находит любые знаки препинания в начале или конце строки, ИГНОРИРУЯ дефисы и апострофы внутри слова
    private static readonly Regex PunctuationCleanRegex = new Regex(@"(^[\p{P}&&[^\-']]+)|([\p{P}&&[^\-']]+$)", RegexOptions.Compiled);

    public string GenerateKaraokeMarkup(List<WordTimeInfo> words)
    {
        if (words == null || words.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        BuildAssHeader(sb);

        var phrases = GroupWordsIntoPhrases(words);
        ExtendPhraseEnds(phrases);
        BuildDialogueLines(sb, phrases);

        return sb.ToString();
    }

    private void BuildAssHeader(StringBuilder sb)
    {
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine("PlayResX: 1080");
        sb.AppendLine("PlayResY: 1920");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        // Изменен размер шрифта с 90 до 68 для размещения двух строк по 40 символов
        sb.AppendLine("Style: KaraokeStyle,Montserrat,68,&H00FFFFFF,&H0000FFFF,&H00000000,&H00000000,-1,0,0,0,100,100,2,0,1,2,0,5,100,100,0,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
    }

    public List<List<WordTimeInfo>> GroupWordsIntoPhrases(List<WordTimeInfo> words)
    {
        var phrases = new List<List<WordTimeInfo>>();
        if (words == null || words.Count == 0) return phrases;

        // Внедряем защитную интерполяцию для выравнивания перекрытий и нулевых длительностей
        var interpolatedWords = InterpolateWordTimings(words);

        var currentBlock = new List<WordTimeInfo>();

        for (int i = 0; i < interpolatedWords.Count; i++)
        {
            var currWord = interpolatedWords[i];

            // УМНАЯ ПОСТОБРАБОТКА: очищаем слово от знаков препинания по краям и переводим в нижний регистр
            string cleanText = PunctuationCleanRegex.Replace(currWord.Text ?? string.Empty, string.Empty).ToLower();

            if (string.IsNullOrWhiteSpace(cleanText)) continue;

            var processedWord = currWord with { Text = cleanText };

            if (currentBlock.Count > 0)
            {
                var prevWord = currentBlock.Last();
                TimeSpan prevEnd = prevWord.End > prevWord.Start ? prevWord.End : prevWord.Start + TimeSpan.FromMilliseconds(200);
                double gapMs = (processedWord.Start - prevEnd).TotalMilliseconds;

                // Если пауза между словами больше 1.5 секунд, начинаем новый блок
                bool isPauseTooLong = gapMs > 1500;

                // Проверяем, помещается ли новое слово в лимит 2 строк по 40 символов
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
        
        // Считаем общую длину, если все в одну строку
        int totalLen = tempWords.Sum(w => w.Text.Length) + tempWords.Count - 1;
        if (totalLen <= maxLineLen) return true;

        // Ищем хотя бы один вариант разбиения на 2 строки, где каждая <= maxLineLen
        for (int k = 1; k < tempWords.Count; k++)
        {
            int line1Len = tempWords.Take(k).Sum(w => w.Text.Length) + k - 1;
            int line2Len = tempWords.Skip(k).Sum(w => w.Text.Length) + (tempWords.Count - k) - 1;

            if (line1Len <= maxLineLen && line2Len <= maxLineLen)
            {
                return true;
            }
        }

        return false;
    }

    private int GetBestSplitIndex(List<WordTimeInfo> blockWords, int maxLineLen)
    {
        int totalLen = blockWords.Sum(w => w.Text.Length) + blockWords.Count - 1;
        if (totalLen <= maxLineLen) return -1; // Не нужно переносить

        int bestK = -1;
        int minDiff = int.MaxValue;

        for (int k = 1; k < blockWords.Count; k++)
        {
            int line1Len = blockWords.Take(k).Sum(w => w.Text.Length) + k - 1;
            int line2Len = blockWords.Skip(k).Sum(w => w.Text.Length) + (blockWords.Count - k) - 1;

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

        // Если не нашли идеального сплита, сплитим по середине
        if (bestK == -1)
        {
            bestK = blockWords.Count / 2;
        }

        return bestK;
    }

    private void CapitalizeFirstWord(List<WordTimeInfo> phrase)
    {
        if (phrase == null || phrase.Count == 0) return;

        var firstWordInfo = phrase[0];
        string word = firstWordInfo.Text;

        if (!string.IsNullOrEmpty(word))
        {
            // Делаем заглавной строго первую букву всей фразы
            string capitalizedWord = char.ToUpper(word[0]) + word[1..];
            phrase[0] = firstWordInfo with { Text = capitalizedWord };
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

            if (desiredEnd > lastWord.Start)
            {
                lastWord.End = desiredEnd;
            }
        }
    }

    private void BuildDialogueLines(StringBuilder sb, List<List<WordTimeInfo>> phrases)
    {
        TimeSpan absoluteMinStart = TimeSpan.Zero;

        for (int i = 0; i < phrases.Count; i++)
        {
            var phrase = phrases[i];

            // 1. Вычисляем фактический конец строки
            TimeSpan lineEnd;
            if (i < phrases.Count - 1)
            {
                var nextPhraseStart = phrases[i + 1].First().Start - TimeShift;
                var phraseActualEnd = phrase.Last().End - TimeShift;
                double cleanGap = (nextPhraseStart - phraseActualEnd).TotalMilliseconds;

                if (cleanGap > InstrumentSilenceThresholdMs)
                {
                    lineEnd = phraseActualEnd + TimeSpan.FromMilliseconds(MaxHoldAfterSpeechMs);
                }
                else
                {
                    lineEnd = nextPhraseStart - TimeSpan.FromMilliseconds(FadeBufferMs);
                }
            }
            else
            {
                lineEnd = phrase.Last().End - TimeShift + TimeSpan.FromMilliseconds(600);
            }

            // 2. Появление заранее (PreRoll)
            var singingStart = phrase.First().Start - TimeShift;
            if (singingStart < TimeSpan.Zero) singingStart = TimeSpan.Zero;

            var lineStart = singingStart - PreRollTime;

            if (lineStart < absoluteMinStart)
            {
                lineStart = absoluteMinStart;
            }

            if (lineStart > singingStart) lineStart = singingStart;
            if (lineEnd < lineStart) lineEnd = lineStart + TimeSpan.FromMilliseconds(500);

            absoluteMinStart = lineEnd + TimeSpan.FromMilliseconds(FadeBufferMs);

            string assStart = FormatTimeSpanForAss(lineStart);
            string assEnd = FormatTimeSpanForAss(lineEnd);

            var lineBuilder = new StringBuilder($"{{\\fad({FadeTimeMs}, {FadeTimeMs})}}");
            TimeSpan currentTime = lineStart;

            // Находим точку сплита на две строки
            int splitIdx = GetBestSplitIndex(phrase, 40);

            for (int j = 0; j < phrase.Count; j++)
            {
                var word = phrase[j];
                var shiftedWordStart = word.Start - TimeShift;
                var shiftedWordEnd = word.End - TimeShift;

                if (shiftedWordStart < TimeSpan.Zero) shiftedWordStart = TimeSpan.Zero;
                if (shiftedWordEnd < shiftedWordStart) shiftedWordEnd = shiftedWordStart;

                if (shiftedWordStart < currentTime)
                {
                    var overlap = currentTime - shiftedWordStart;
                    shiftedWordStart = currentTime;
                    shiftedWordEnd += overlap;
                }

                var pauseDuration = shiftedWordStart - currentTime;
                if (pauseDuration.TotalMilliseconds > 10)
                {
                    int pauseCs = (int)Math.Round(pauseDuration.TotalMilliseconds / 10.0);
                    if (pauseCs > 0) lineBuilder.Append($"{{\\kf{pauseCs}}}");
                }

                int wordCs = (int)Math.Round((shiftedWordEnd - shiftedWordStart).TotalMilliseconds / 10.0);
                if (wordCs <= 0) wordCs = 1;

                // Перенос строки перед текущим словом, если достигли splitIdx
                if (j == splitIdx)
                {
                    lineBuilder.Append("\\N");
                }

                // В конце строки пробел не нужен, также как и перед переносом строки \N
                string trailingSpace = (j != phrase.Count - 1 && j != splitIdx - 1) ? " " : "";
                lineBuilder.Append($"{{\\kf{wordCs}}}{word.Text}{trailingSpace}");

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

    private List<WordTimeInfo> InterpolateWordTimings(List<WordTimeInfo> words)
    {
        if (words == null || words.Count == 0) return new List<WordTimeInfo>();

        var cleanWords = new List<WordTimeInfo>();
        foreach (var w in words)
        {
            cleanWords.Add(w with { });
        }

        // 1. Исправляем нулевую или отрицательную длительность каждого отдельного слова
        for (int i = 0; i < cleanWords.Count; i++)
        {
            if (cleanWords[i].End <= cleanWords[i].Start)
            {
                cleanWords[i].End = cleanWords[i].Start.Add(TimeSpan.FromMilliseconds(150));
            }
        }

        // 2. Исправляем перекрытия и взаимное наложение слов
        int idx = 0;
        while (idx < cleanWords.Count)
        {
            int startIdx = idx;
            TimeSpan maxTime = cleanWords[idx].End;
            int endIdx = idx;

            while (endIdx + 1 < cleanWords.Count && cleanWords[endIdx + 1].Start < maxTime)
            {
                endIdx++;
                if (cleanWords[endIdx].End > maxTime)
                {
                    maxTime = cleanWords[endIdx].End;
                }
            }

            if (endIdx > startIdx)
            {
                TimeSpan totalSpanStart = cleanWords[startIdx].Start;
                TimeSpan totalSpanEnd = maxTime;
                double totalDurationMs = (totalSpanEnd - totalSpanStart).TotalMilliseconds;

                double totalLength = 0;
                for (int i = startIdx; i <= endIdx; i++)
                {
                    totalLength += Math.Max(1, cleanWords[i].Text.Length);
                }

                double elapsedMs = 0;
                for (int i = startIdx; i <= endIdx; i++)
                {
                    double wordLen = Math.Max(1, cleanWords[i].Text.Length);
                    double wordDurationMs = (wordLen / totalLength) * totalDurationMs;

                    if (wordDurationMs < 100) wordDurationMs = 100;

                    cleanWords[i].Start = totalSpanStart.Add(TimeSpan.FromMilliseconds(elapsedMs));
                    cleanWords[i].End = cleanWords[i].Start.Add(TimeSpan.FromMilliseconds(wordDurationMs));

                    elapsedMs += wordDurationMs;
                }
            }

            idx = endIdx + 1;
        }

        // Итоговая хронологическая коррекция
        for (int i = 1; i < cleanWords.Count; i++)
        {
            if (cleanWords[i].Start < cleanWords[i - 1].End)
            {
                cleanWords[i].Start = cleanWords[i - 1].End;
                if (cleanWords[i].End <= cleanWords[i].Start)
                {
                    cleanWords[i].End = cleanWords[i].Start.Add(TimeSpan.FromMilliseconds(150));
                }
            }
        }

        return cleanWords;
    }
}