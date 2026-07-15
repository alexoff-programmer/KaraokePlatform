using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Records;
namespace KaraokePlatform.Services.Audio.Interfaces;

public interface ISpeechRecognizer
{
    Task<List<WordTimeInfo>> TranscribeAndMergeTokensAsync(
        string wavPath,
        string language,
        Action<int> onProgress,
        string? geminiApiKey = null,
        string? trackName = null,
        List<AudioInterval>? vadIntervals = null);
}