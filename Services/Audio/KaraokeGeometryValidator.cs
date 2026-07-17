using System;
using System.Collections.Generic;
using System.Linq;
using KaraokePlatform.Services.Audio.Records;

namespace KaraokePlatform.Services.Audio;

public static class KaraokeGeometryValidator
{
    public static List<WordTimeInfo> ValidateAndCorrect(List<WordTimeInfo> words, TimeSpan phraseStart, TimeSpan phraseEnd)
    {
        if (words == null || words.Count == 0) return new List<WordTimeInfo>();

        var cleanWords = words.Select(w => new WordTimeInfo { Text = w.Text, Start = w.Start, End = w.End }).ToList();

        // 1. Initial pass: calculate minimum durations and hold within phrase bounds
        for (int i = 0; i < cleanWords.Count; i++)
        {
            if (cleanWords[i].Start < phraseStart) cleanWords[i].Start = phraseStart;
            if (cleanWords[i].End > phraseEnd) cleanWords[i].End = phraseEnd;

            double minDurationMs = Math.Max(60.0, GetCleanTextLength(cleanWords[i].Text) * 15.0);
            var minDuration = TimeSpan.FromMilliseconds(minDurationMs);

            if (cleanWords[i].Duration < minDuration)
            {
                cleanWords[i].End = cleanWords[i].Start + minDuration;
            }

            if (cleanWords[i].End > phraseEnd)
            {
                cleanWords[i].End = phraseEnd;
                cleanWords[i].Start = cleanWords[i].End - minDuration;
                if (cleanWords[i].Start < phraseStart) cleanWords[i].Start = phraseStart;
            }
        }

        // 2. Resolve overlaps left-to-right
        for (int i = 1; i < cleanWords.Count; i++)
        {
            if (cleanWords[i].Start < cleanWords[i - 1].End)
            {
                // If we can just shrink the previous word's end without violating its min duration:
                double prevMinMs = Math.Max(60.0, GetCleanTextLength(cleanWords[i - 1].Text) * 15.0);
                var prevMin = TimeSpan.FromMilliseconds(prevMinMs);

                if (cleanWords[i].Start - cleanWords[i - 1].Start >= prevMin)
                {
                    cleanWords[i - 1].End = cleanWords[i].Start;
                }
                else
                {
                    // Shrink previous word to its minimum duration
                    cleanWords[i - 1].End = cleanWords[i - 1].Start + prevMin;
                    // Shift current word start to the end of previous
                    var duration = cleanWords[i].Duration;
                    cleanWords[i].Start = cleanWords[i - 1].End;
                    cleanWords[i].End = cleanWords[i].Start + duration;
                }
            }
        }

        // 3. Resolve overlaps right-to-left if the last words were pushed past phraseEnd
        if (cleanWords.Count > 0 && cleanWords[^1].End > phraseEnd)
        {
            cleanWords[^1].End = phraseEnd;
            double lastMinMs = Math.Max(60.0, GetCleanTextLength(cleanWords[^1].Text) * 15.0);
            cleanWords[^1].Start = cleanWords[^1].End - TimeSpan.FromMilliseconds(lastMinMs);
            if (cleanWords[^1].Start < phraseStart) cleanWords[^1].Start = phraseStart;

            for (int i = cleanWords.Count - 2; i >= 0; i--)
            {
                if (cleanWords[i].End > cleanWords[i + 1].Start)
                {
                    cleanWords[i].End = cleanWords[i + 1].Start;
                    double minMs = Math.Max(60.0, GetCleanTextLength(cleanWords[i].Text) * 15.0);
                    cleanWords[i].Start = cleanWords[i].End - TimeSpan.FromMilliseconds(minMs);
                    if (cleanWords[i].Start < phraseStart) cleanWords[i].Start = phraseStart;
                }
            }
        }

        return cleanWords;
    }

    private static int GetCleanTextLength(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Count(c => char.IsLetterOrDigit(c));
    }
}
