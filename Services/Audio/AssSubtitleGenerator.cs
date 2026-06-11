using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KaraokePlatform.Services.Audio.Interfaces;
using KaraokePlatform.Services.Audio.Records;

namespace KaraokePlatform.Services.Audio;

public class AssSubtitleGenerator : ISubtitleGenerator
{
    private static readonly TimeSpan TimeShift = TimeSpan.FromMilliseconds(200);

    private const double InstrumentSilenceThresholdMs = 1200; // Если впереди тишина от инструментала больше 1.2 секунды, держим строку на экране, чтобы не обрывать ее раньше времени

    // Настройки аккуратного последовательного отображения без каши
    private int FadeTimeMs = 0;          // Мягкое появление/исчезновение (четверть секунды)
    private int FadeBufferMs = 0;        // Железный зазор между блоками субтитров
    private int MaxHoldAfterSpeechMs = 500; // Сколько удерживать строку, если впереди долгая пауза

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

    private List<List<WordTimeInfo>> GroupWordsIntoPhrases(List<WordTimeInfo> words)
    {
        var phrases = new List<List<WordTimeInfo>>();
        var currentPhrase = new List<WordTimeInfo> { words[0] };

        for (int i = 1; i < words.Count; i++)
        {
            var prevWord = words[i - 1];
            var currWord = words[i];

            TimeSpan prevEnd = prevWord.End > prevWord.Start ? prevWord.End : prevWord.Start + TimeSpan.FromMilliseconds(200);
            double originalGapMs = (currWord.Start - prevEnd).TotalMilliseconds;

            // Триггер новой строки: пауза > 280мс или лимит в 4 слова
            if (originalGapMs > 280 || currentPhrase.Count >= 4)
            {
                phrases.Add(currentPhrase);
                currentPhrase = new List<WordTimeInfo>();
            }
            currentPhrase.Add(currWord);
        }
        if (currentPhrase.Count > 0) phrases.Add(currentPhrase);
        return phrases;
    }

    private void ExtendPhraseEnds(List<List<WordTimeInfo>> phrases)
    {
        for (int i = 0; i < phrases.Count; i++)
        {
            var currentPhraseWords = phrases[i];
            var lastWord = currentPhraseWords.Last();

            // Строго следим, чтобы конец текущей фразы не налезал на старт следующей с учетом буфера
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
        for (int i = 0; i < phrases.Count; i++)
        {
            var phrase = phrases[i];

            // Старт строго по началу пения (никаких забеганий назад)
            var lineStart = phrase.First().Start - TimeShift;
            if (lineStart < TimeSpan.Zero) lineStart = TimeSpan.Zero;

            TimeSpan lineEnd;
            if (i < phrases.Count - 1)
            {
                var nextPhraseStart = phrases[i + 1].First().Start - TimeShift;
                var phraseActualEnd = phrase.Last().End - TimeShift;
                double cleanGap = (nextPhraseStart - phraseActualEnd).TotalMilliseconds;

                // Если впереди длинный музыкальный проигрыш, держим строку не дольше MaxHoldAfterSpeechMs
                if (cleanGap > InstrumentSilenceThresholdMs)
                {
                    lineEnd = phraseActualEnd + TimeSpan.FromMilliseconds(MaxHoldAfterSpeechMs);
                }
                else
                {
                    // Если фразы идут близко, тушим текущую строго до старта следующей за вычетом FadeBuffer
                    lineEnd = nextPhraseStart - TimeSpan.FromMilliseconds(FadeBufferMs);
                }
            }
            else
            {
                lineEnd = phrase.Last().End - TimeShift + TimeSpan.FromMilliseconds(600);
            }

            // Страховка от вырождения таймингов
            if (lineEnd < lineStart) lineEnd = lineStart + TimeSpan.FromMilliseconds(500);

            string assStart = FormatTimeSpanForAss(lineStart);
            string assEnd = FormatTimeSpanForAss(lineEnd);

            // Применяем легкий, не ломающий чтение фейд
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

            // Корректно закрываем караоке-таймлайн до физического конца строки ASS
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
        // Атомарный расчет центисекунд во избежание каскадного переполнения секунд/минут
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