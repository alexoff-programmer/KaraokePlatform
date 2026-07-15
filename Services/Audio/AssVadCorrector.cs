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

            var originalWhisperStart = phrase.First().Start;
            var phraseEnd = phrase.Last().End;
            var phraseStart = originalWhisperStart;

            var correctedWords = KaraokeGeometryValidator.ValidateAndCorrect(phrase, phraseStart, phraseEnd);
            if (correctedWords.Count > 0)
            {
                correctedPhrases.Add(correctedWords);
            }
        }
        return correctedPhrases;
    }
}