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
            TimeSpan phraseStart;

            // Find overlapping VAD intervals
            var overlapping = vadIntervals.Where(v => v.Start < phraseEnd && v.End > originalWhisperStart).ToList();
            if (overlapping.Count > 0)
            {
                var vadStart = overlapping.Min(v => v.Start);

                double vadWeight = 0.9;
                double whisperWeight = 0.1;

                double vadSeconds = vadStart.TotalSeconds;
                double whisperSeconds = originalWhisperStart.TotalSeconds;
                double blendedSeconds = (vadSeconds * vadWeight) + (whisperSeconds * whisperWeight);
                var blendedStart = TimeSpan.FromSeconds(blendedSeconds);
                phraseStart = blendedStart < phraseEnd ? blendedStart : vadStart;

                phraseEnd = overlapping.Max(v => v.End);
            }
            else
            {
                phraseStart = originalWhisperStart;
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