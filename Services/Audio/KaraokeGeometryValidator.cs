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

        // 1. Ensure all words are within the phrase boundaries [phraseStart, phraseEnd]
        for (int i = 0; i < cleanWords.Count; i++)
        {
            if (cleanWords[i].Start < phraseStart) cleanWords[i].Start = phraseStart;
            if (cleanWords[i].End > phraseEnd) cleanWords[i].End = phraseEnd;

            double minDurationMs = Math.Max(60.0, cleanWords[i].Text.Length * 15.0);
            if (cleanWords[i].End <= cleanWords[i].Start || (cleanWords[i].End - cleanWords[i].Start).TotalMilliseconds < minDurationMs)
            {
                cleanWords[i].End = cleanWords[i].Start.Add(TimeSpan.FromMilliseconds(minDurationMs));
            }
        }

        // 2. Softly adjust boundaries if there are overlaps
        for (int i = 1; i < cleanWords.Count; i++)
        {
            if (cleanWords[i].Start < cleanWords[i - 1].End)
            {
                if (cleanWords[i].Start > cleanWords[i - 1].Start)
                {
                    // Просто сдвигаем конец предыдущего слова на начало текущего
                    cleanWords[i - 1].End = cleanWords[i].Start;
                }
                else
                {
                    cleanWords[i].Start = cleanWords[i - 1].End;
                    if (cleanWords[i].End <= cleanWords[i].Start)
                    {
                        cleanWords[i].End = cleanWords[i].Start.Add(TimeSpan.FromMilliseconds(100));
                    }
                }
            }
        }

        // 3. Strictly enforce adjacent boundaries: Word[i].Start == Word[i-1].End
        for (int i = 1; i < cleanWords.Count; i++)
        {
            cleanWords[i].Start = cleanWords[i - 1].End;

            double minDurationMs = Math.Max(60.0, cleanWords[i].Text.Length * 15.0);
            if (cleanWords[i].End <= cleanWords[i].Start || (cleanWords[i].End - cleanWords[i].Start).TotalMilliseconds < minDurationMs)
            {
                cleanWords[i].End = cleanWords[i].Start.Add(TimeSpan.FromMilliseconds(minDurationMs));
            }
        }

        // 4. Ensure last word's end aligns exactly with the speech chunk end
        if (cleanWords.Count > 0)
        {
            var lastWord = cleanWords[^1];
            if (lastWord.End != phraseEnd && phraseEnd > lastWord.Start)
            {
                lastWord.End = phraseEnd;
            }
        }

        return cleanWords;
    }
}
