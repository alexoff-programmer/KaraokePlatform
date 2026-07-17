using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Records;
namespace KaraokePlatform.Services.Audio.Interfaces;

public interface ISpeechRecognizer
{
    Task<(List<WordTimeInfo> Words, string Language)> TranscribeAndMergeTokensAsync(
        string wavPath,
        string language,
        Action<int> onProgress,
        string? geminiApiKey = null,
        string? trackName = null,
        List<AudioInterval>? vadIntervals = null);

    Task<(List<WordTimeInfo> Words, string Language)> TranscribeSamplesAsync(
        float[] samples,
        string language,
        string? geminiApiKey = null,
        string? trackName = null);
}