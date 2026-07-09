using System;
using System.Collections.Generic;
using System.Linq;
using KaraokePlatform.Services.Audio.Records;

namespace KaraokePlatform.Services.Audio;

public static class AssVadCorrector
{
    public static List<List<WordTimeInfo>> Correct(List<List<WordTimeInfo>> phrases, List<AudioInterval> vadIntervals)
    {
        var correctedPhrases = new List<List<WordTimeInfo>>();
        foreach (var phrase in phrases)
        {
            if (phrase == null || phrase.Count == 0) continue;

            var phraseStart = phrase.First().Start;
            var phraseEnd = phrase.Last().End;

            // Find overlapping VAD intervals
            var overlapping = vadIntervals.Where(v => v.Start < phraseEnd && v.End > phraseStart).ToList();
            if (overlapping.Count > 0)
            {
                phraseStart = overlapping.Min(v => v.Start);
                phraseEnd = overlapping.Max(v => v.End);
            }

            var correctedWords = KaraokeGeometryValidator.ValidateAndCorrect(phrase, phraseStart, phraseEnd);
            if (correctedWords.Count > 0)
            {
                correctedPhrases.Add(correctedWords);
            }
        }
        return correctedPhrases;
    }
}
