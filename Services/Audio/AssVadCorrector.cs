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
        if (phrases == null || phrases.Count == 0) return correctedPhrases;

        foreach (var phrase in phrases)
        {
            if (phrase == null || phrase.Count == 0) continue;

            var originalWhisperStart = phrase.First().Start;
            var originalWhisperEnd = phrase.Last().End;

            var correctedStart = originalWhisperStart;
            var correctedEnd = originalWhisperEnd;

            if (vadIntervals != null && vadIntervals.Count > 0)
            {
                // Find VAD interval whose start is closest to originalWhisperStart (within 1.5 seconds)
                var startVad = vadIntervals
                    .Where(v => Math.Abs((v.Start - originalWhisperStart).TotalSeconds) <= 1.5)
                    .OrderBy(v => Math.Abs((v.Start - originalWhisperStart).TotalSeconds))
                    .FirstOrDefault();

                if (startVad != null)
                {
                    correctedStart = startVad.Start;
                }

                // Find VAD interval whose end is closest to originalWhisperEnd (within 1.5 seconds)
                var endVad = vadIntervals
                    .Where(v => Math.Abs((v.End - originalWhisperEnd).TotalSeconds) <= 1.5)
                    .OrderBy(v => Math.Abs((v.End - originalWhisperEnd).TotalSeconds))
                    .FirstOrDefault();

                if (endVad != null)
                {
                    correctedEnd = endVad.End;
                }
            }

            if (correctedEnd <= correctedStart)
            {
                correctedEnd = correctedStart.Add(TimeSpan.FromMilliseconds(200));
            }

            // Adjust first word start and last word end
            phrase.First().Start = correctedStart;
            phrase.Last().End = correctedEnd;

            // Make sure word timings inside the phrase remain monotonically increasing and valid
            var correctedWords = KaraokeGeometryValidator.ValidateAndCorrect(phrase, correctedStart, correctedEnd);
            if (correctedWords.Count > 0)
            {
                correctedPhrases.Add(correctedWords);
            }
        }
        return correctedPhrases;
    }
}