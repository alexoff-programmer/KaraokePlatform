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
        sb.AppendLine("Style: KaraokeStyle,Montserrat,90,&H00FFFFFF,&H0000FFFF,&H00000000,&H00000000,-1,0,0,0,100,100,2,0,1,2,0,5,100,100,0,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
    }

    public List<List<WordTimeInfo>> GroupWordsIntoPhrases(List<WordTimeInfo> words)
    {
        var phrases = new List<List<WordTimeInfo>>();
        if (words == null || words.Count == 0) return phrases;

        // Создаем чистый список для первой фразы
        var currentPhrase = new List<WordTimeInfo>();
        int currentPhraseChars = 0;

        for (int i = 0; i < words.Count; i++)
        {
            var currWord = words[i];

            // УМНАЯ ПОСТОБРАБОТКА: очищаем слово от знаков препинания по краям и переводим в нижний регистр
            string cleanText = PunctuationCleanRegex.Replace(currWord.Text ?? string.Empty, string.Empty).ToLower();

            // Если после очистки слово стало пустым (например, это был одиночный знак «...» или «?»), пропускаем его
            if (string.IsNullOrWhiteSpace(cleanText)) continue;

            // Создаем новый объект с чистым текстом
            var processedWord = currWord with { Text = cleanText };

            if (currentPhrase.Count > 0)
            {
                var prevWord = currentPhrase.Last();
                TimeSpan prevEnd = prevWord.End > prevWord.Start ? prevWord.End : prevWord.Start + TimeSpan.FromMilliseconds(200);
                double originalGapMs = (processedWord.Start - prevEnd).TotalMilliseconds;

                bool isLongPause = originalGapMs > 450;
                bool isHugeGap = originalGapMs > 2500;
                bool isLineTooLong = currentPhrase.Count >= 6 || currentPhraseChars > 25;

                if ((isLongPause && currentPhrase.Count >= 3) || isLineTooLong || isHugeGap)
                {
                    CapitalizeFirstWord(currentPhrase);
                    phrases.Add(currentPhrase);
                    currentPhrase = new List<WordTimeInfo>();
                    currentPhraseChars = 0;
                }
            }

            currentPhrase.Add(processedWord);
            currentPhraseChars += processedWord.Text.Length + 1; // +1 для пробела
        }

        if (currentPhrase.Count > 0)
        {
            CapitalizeFirstWord(currentPhrase);
            phrases.Add(currentPhrase);
        }

        return phrases;
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

            // Сборка слов фразы
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

                string trailingSpace = (j != phrase.Count - 1) ? " " : "";
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
}