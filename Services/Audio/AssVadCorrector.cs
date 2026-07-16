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
            var phraseEnd = phrase.Last().End;
            var phraseStart = originalWhisperStart;

            if (vadIntervals != null && vadIntervals.Count > 0)
            {
                // Поиск перекрывающихся интервалов VAD с допуском 1.5 секунды
                var tolerance = TimeSpan.FromSeconds(1.5);
                var overlappingVads = vadIntervals.Where(v =>
                    v.Start <= phraseEnd + tolerance &&
                    v.End >= phraseStart - tolerance
                ).ToList();

                if (overlappingVads.Count > 0)
                {
                    var vadStart = overlappingVads.Min(v => v.Start);
                    var vadEnd = overlappingVads.Max(v => v.End);

                    // Условие: silero vad видит голос И whisper поставил тайминг (выбираем максимум)
                    var correctedStart = vadStart > originalWhisperStart ? vadStart : originalWhisperStart;
                    var correctedEnd = vadEnd;

                    if (correctedEnd <= correctedStart)
                    {
                        correctedEnd = correctedStart.Add(TimeSpan.FromMilliseconds(200));
                    }

                    var originalDuration = phraseEnd - phraseStart;
                    var newDuration = correctedEnd - correctedStart;

                    if (originalDuration.TotalMilliseconds > 0 && newDuration.TotalMilliseconds > 0)
                    {
                        double ratio = newDuration.TotalMilliseconds / originalDuration.TotalMilliseconds;
                        foreach (var word in phrase)
                        {
                            var relStartMs = (word.Start - phraseStart).TotalMilliseconds * ratio;
                            var relEndMs = (word.End - phraseStart).TotalMilliseconds * ratio;

                            word.Start = correctedStart + TimeSpan.FromMilliseconds(relStartMs);
                            word.End = correctedStart + TimeSpan.FromMilliseconds(relEndMs);
                        }
                    }
                    else
                    {
                        foreach (var word in phrase)
                        {
                            word.Start = correctedStart;
                            word.End = correctedEnd;
                        }
                    }

                    phraseStart = correctedStart;
                    phraseEnd = correctedEnd;
                }
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