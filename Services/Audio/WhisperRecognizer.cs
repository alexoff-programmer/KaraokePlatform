using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Interfaces;
using KaraokePlatform.Services.Audio.Records;
using KaraokePlatform.Settings;
using Microsoft.Extensions.Options;
using Whisper.net;

namespace KaraokePlatform.Services.Audio;

public class WhisperRecognizer : ISpeechRecognizer
{
    private readonly string _modelPath;
    private const float NoSpeechThreshold = 0.45f;

    public WhisperRecognizer(IOptions<WhisperSettings> settings)
    {
        _modelPath = settings.Value.ModelPath;
    }

    public async Task<List<WordTimeInfo>> TranscribeAndMergeTokensAsync(
        string wavPath,
        string language,
        List<(TimeSpan Start, TimeSpan End)> vocalIntervals,
        Action<int> onProgress)
    {
        var allWords = new List<WordTimeInfo>();

        using var factory = WhisperFactory.FromPath(_modelPath);
        using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithTokenTimestamps()
            .WithPrompt("Караоке, текст песни, точные слова и вокальные фразы.")
            .WithProgressHandler(onProgress.Invoke)
            .WithNoContext()
            .Build();

        using var fileStream = File.OpenRead(wavPath);

        await foreach (var segment in processor.ProcessAsync(fileStream))
        {
            var rawText = segment.Text.Trim();

            var cleanText = Regex.Replace(rawText, @"\[.*?\]|\(.*?\)|♪", "").Trim();
            cleanText = cleanText.Replace("ё", "е").Replace("Ё", "Е");

            // УЛУЧШЕНИЕ: Превращаем дефисы в пробелы для точного совпадения с BPE-токенами Whisper
            cleanText = cleanText.Replace("-", " ");

            if (string.IsNullOrWhiteSpace(cleanText) || IsHallucination(cleanText))
            {
                continue;
            }

            var expectedWords = cleanText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (expectedWords.Length == 0) continue;

            var segmentWords = ProcessSegmentTokens(segment, expectedWords);

            AdjustAndInterpolateTimings(segmentWords, segment, segment.NoSpeechProbability, vocalIntervals);

            allWords.AddRange(segmentWords);
        }

        return allWords;
    }

    private bool IsHallucination(string text)
    {
        return text.Contains("Редактор субтитров") ||
               text.Contains("Субтитры созданы") ||
               text.Contains("Корректор") ||
               text.Equals("музыка", StringComparison.OrdinalIgnoreCase);
    }

    private List<WordTimeInfo> ProcessSegmentTokens(SegmentData segment, string[] expectedWords)
    {
        var segmentWords = new List<WordTimeInfo>();
        var tokens = segment.Tokens ?? Array.Empty<WhisperToken>();

        bool isRelativeTokens = false;
        if (tokens.Length > 0 && segment.Start.TotalMilliseconds > 150)
        {
            var firstTextToken = tokens.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text) &&
                                                           !t.Text.StartsWith("[") &&
                                                           !t.Text.StartsWith("<"));
            if (firstTextToken != null)
            {
                long tokenStartMs = firstTextToken.Start * 10;
                if (tokenStartMs < segment.Start.TotalMilliseconds - 100)
                {
                    isRelativeTokens = true;
                }
            }
        }

        int tokenIndex = 0;
        string activeTokenText = "";
        TimeSpan? activeTokenStart = null;
        TimeSpan? activeTokenEnd = null;

        foreach (var word in expectedWords)
        {
            var wordInfo = new WordTimeInfo { Text = word };
            string cleanTargetWord = string.Concat(word.Where(char.IsLetterOrDigit)).ToLower();

            if (string.IsNullOrEmpty(cleanTargetWord))
            {
                TimeSpan tokenTime = tokenIndex < tokens.Length
                    ? GetAbsoluteTokenTime(tokens[tokenIndex].Start, segment.Start, isRelativeTokens)
                    : (segmentWords.Count > 0 ? segmentWords.Last().End : segment.Start);

                wordInfo.Start = tokenTime;
                wordInfo.End = tokenTime + TimeSpan.FromMilliseconds(50);
                segmentWords.Add(wordInfo);
                continue;
            }

            var currentWordProgress = new StringBuilder();
            TimeSpan? wordStart = null;
            TimeSpan? wordEnd = null;

            // УЛУЧШЕНИЕ: Защита от каскадного зависания при расхождениях в токенах
            int maxTokenAttempts = tokens.Length - tokenIndex + 1;
            int attempts = 0;

            while (currentWordProgress.Length < cleanTargetWord.Length && attempts < maxTokenAttempts)
            {
                attempts++;
                if (string.IsNullOrEmpty(activeTokenText))
                {
                    if (tokenIndex >= tokens.Length) break;

                    var token = tokens[tokenIndex];
                    tokenIndex++;

                    var tText = token.Text?.Replace(" ", "") ?? "";
                    bool isServiceToken = (tText.StartsWith("[") && tText.EndsWith("]")) ||
                                          (tText.StartsWith("<") && tText.EndsWith(">")) ||
                                          tText.Contains("♪");

                    if (string.IsNullOrEmpty(tText) || isServiceToken) continue;

                    activeTokenText = string.Concat(tText.Where(char.IsLetterOrDigit)).ToLower();
                    activeTokenText = activeTokenText.Replace("ё", "е");

                    activeTokenStart = GetAbsoluteTokenTime(token.Start, segment.Start, isRelativeTokens);
                    activeTokenEnd = GetAbsoluteTokenTime(token.End, segment.Start, isRelativeTokens);

                    if (string.IsNullOrEmpty(activeTokenText))
                    {
                        if (wordStart != null) wordEnd = activeTokenEnd;
                        continue;
                    }
                }

                if (wordStart == null) wordStart = activeTokenStart;
                wordEnd = activeTokenEnd;

                int neededLength = cleanTargetWord.Length - currentWordProgress.Length;
                if (activeTokenText.Length <= neededLength)
                {
                    currentWordProgress.Append(activeTokenText);
                    activeTokenText = "";
                }
                else
                {
                    currentWordProgress.Append(activeTokenText.Substring(0, neededLength));
                    activeTokenText = activeTokenText.Substring(neededLength);
                }
            }

            if (wordStart == null)
            {
                TimeSpan fallbackStart = segmentWords.Count > 0 ? segmentWords.Last().End : segment.Start;

                if (tokenIndex < tokens.Length)
                {
                    TimeSpan currentTokenTime = GetAbsoluteTokenTime(tokens[tokenIndex].Start, segment.Start, isRelativeTokens);
                    if (currentTokenTime > fallbackStart)
                    {
                        fallbackStart = currentTokenTime;
                    }
                }

                wordInfo.Start = fallbackStart;
                wordInfo.End = wordInfo.Start + TimeSpan.FromMilliseconds(Math.Max(200, word.Length * 90));
            }
            else
            {
                wordInfo.Start = wordStart.Value;
                wordInfo.End = wordEnd ?? (wordInfo.Start + TimeSpan.FromMilliseconds(150));
            }

            if (wordInfo.End <= wordInfo.Start)
            {
                wordInfo.End = wordInfo.Start + TimeSpan.FromMilliseconds(150);
            }

            segmentWords.Add(wordInfo);
        }

        return segmentWords;
    }

    private void AdjustAndInterpolateTimings(
    List<WordTimeInfo> segmentWords,
    SegmentData segment,
    float noSpeechProb,
    List<(TimeSpan Start, TimeSpan End)> vocalIntervals)
    {
        if (segmentWords.Count == 0) return;

        TimeSpan effectiveSegmentStart = segment.Start;
        TimeSpan effectiveSegmentEnd = segment.End;

        // ИСПРАВЛЕНО: Проверяем, есть ли вообще пересечения с картой вокала
        var intersectingIntervals = vocalIntervals
            .Where(i => i.End > segment.Start && i.Start < segment.End)
            .ToList();

        // Запасной план: если карта вокала пустая (тихий трек/интро), 
        // мы НЕ душим алгоритм, а берем оригинальные границы сегмента Whisper
        if (intersectingIntervals.Count > 0)
        {
            effectiveSegmentStart = intersectingIntervals.First().Start;
            effectiveSegmentEnd = intersectingIntervals.Last().End;

            if (effectiveSegmentStart < segment.Start) effectiveSegmentStart = segment.Start;
            if (effectiveSegmentEnd > segment.End) effectiveSegmentEnd = segment.End;
        }

        if (noSpeechProb > NoSpeechThreshold)
        {
            var lastWord = segmentWords.Last();
            int estimatedMaxDurationMs = Math.Clamp(lastWord.Text.Length * 300, 250, 1200);
            TimeSpan maxEndByText = lastWord.Start + TimeSpan.FromMilliseconds(estimatedMaxDurationMs);

            if (effectiveSegmentEnd > maxEndByText && maxEndByText > effectiveSegmentStart)
            {
                effectiveSegmentEnd = maxEndByText;
            }
        }

        bool needsInterpolation = segmentWords.Count > 1 &&
            segmentWords.Count(w => w.Start == segment.Start || w.End == segment.End) > segmentWords.Count / 2;

        if (!needsInterpolation && segmentWords.Any(w => w.End > effectiveSegmentEnd || w.Start < effectiveSegmentStart))
        {
            needsInterpolation = true;
        }

        if (needsInterpolation)
        {
            double totalChars = segmentWords.Sum(w => w.Text.Length);
            TimeSpan currentTrackTime = effectiveSegmentStart;
            TimeSpan segmentDuration = effectiveSegmentEnd - effectiveSegmentStart;

            if (segmentDuration.TotalMilliseconds <= 0)
                segmentDuration = TimeSpan.FromMilliseconds(segmentWords.Count * 200);

            for (int i = 0; i < segmentWords.Count; i++)
            {
                double weight = segmentWords[i].Text.Length / totalChars;
                double wordDurationMs = segmentDuration.TotalMilliseconds * weight;

                segmentWords[i].Start = currentTrackTime;
                segmentWords[i].End = currentTrackTime + TimeSpan.FromMilliseconds(wordDurationMs);
                currentTrackTime = segmentWords[i].End;
            }
        }
        else
        {
            for (int i = 0; i < segmentWords.Count; i++)
            {
                var currentWord = segmentWords[i];

                // Применяем индивидуальный VAD только если карта активности не пуста
                if (intersectingIntervals.Count > 0)
                {
                    var wordVad = vocalIntervals.FirstOrDefault(intv => intv.End > currentWord.Start && intv.Start < currentWord.End);
                    if (wordVad != default)
                    {
                        if (currentWord.Start < wordVad.Start) currentWord.Start = wordVad.Start;
                        if (currentWord.End > wordVad.End) currentWord.End = wordVad.End;
                    }
                }

                if (currentWord.Start < effectiveSegmentStart) currentWord.Start = effectiveSegmentStart;
                if (currentWord.End > effectiveSegmentEnd) currentWord.End = effectiveSegmentEnd;
                if (currentWord.Start >= currentWord.End) currentWord.Start = currentWord.End - TimeSpan.FromMilliseconds(100);

                double wordDurationMs = (currentWord.End - currentWord.Start).TotalMilliseconds;

                if (wordDurationMs > 2500 || currentWord.End <= currentWord.Start)
                {
                    TimeSpan prevEnd = (i > 0) ? segmentWords[i - 1].End : effectiveSegmentStart;
                    currentWord.Start = prevEnd;

                    int expectedMs = Math.Clamp(currentWord.Text.Length * 120, 180, 1500);
                    currentWord.End = currentWord.Start + TimeSpan.FromMilliseconds(expectedMs);
                }

                if (i > 0 && currentWord.Start < segmentWords[i - 1].End)
                {
                    if (segmentWords[i - 1].End < currentWord.End)
                    {
                        currentWord.Start = segmentWords[i - 1].End;
                    }
                    else
                    {
                        var minAllowedEnd = segmentWords[i - 1].Start + TimeSpan.FromMilliseconds(80);
                        var targetMidpoint = TimeSpan.FromMilliseconds((currentWord.Start.TotalMilliseconds + currentWord.End.TotalMilliseconds) / 2);

                        if (targetMidpoint > minAllowedEnd && targetMidpoint < currentWord.End)
                        {
                            segmentWords[i - 1].End = targetMidpoint;
                            currentWord.Start = targetMidpoint;
                        }
                        else
                        {
                            segmentWords[i - 1].End = currentWord.Start;
                        }
                    }
                }
            }
        }
    }

    private TimeSpan GetAbsoluteTokenTime(long tokenTimeValue, TimeSpan segmentStart, bool isRelative)
    {
        TimeSpan tokenTime = TimeSpan.FromMilliseconds(tokenTimeValue * 10);
        return isRelative ? segmentStart + tokenTime : tokenTime;
    }
}