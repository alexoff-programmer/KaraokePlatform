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

        // 1. Удержание в рамках фраз
        for (int i = 0; i < cleanWords.Count; i++)
        {
            if (cleanWords[i].Start < phraseStart) cleanWords[i].Start = phraseStart;
            if (cleanWords[i].End > phraseEnd) cleanWords[i].End = phraseEnd;
            if (cleanWords[i].End <= cleanWords[i].Start)
            {
                double minDurationMs = Math.Max(60.0, (cleanWords[i].Text ?? "").Length * 15.0);
                cleanWords[i].End = cleanWords[i].Start.Add(TimeSpan.FromMilliseconds(minDurationMs));
            }
        }

        // 2. Исправление только НАЛОЖЕНИЙ (паузы НЕ трогаем!)
        for (int i = 1; i < cleanWords.Count; i++)
        {
            if (cleanWords[i].Start < cleanWords[i - 1].End)
            {
                if (cleanWords[i].Start > cleanWords[i - 1].Start)
                {
                    cleanWords[i - 1].End = cleanWords[i].Start;
                }
                else
                {
                    cleanWords[i].Start = cleanWords[i - 1].End;
                    if (cleanWords[i].End <= cleanWords[i].Start)
                    {
                        double minDurationMs = Math.Max(60.0, (cleanWords[i].Text ?? "").Length * 15.0);
                        cleanWords[i].End = cleanWords[i].Start.Add(TimeSpan.FromMilliseconds(minDurationMs));
                    }
                }
            }
        }

        if (cleanWords.Count > 0 && cleanWords[^1].End > phraseEnd)
        {
            cleanWords[^1].End = phraseEnd;
        }

        return cleanWords;
    }
}
