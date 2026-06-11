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
        List<(TimeSpan Start, TimeSpan End)> vocalIntervals,
        Action<int> onProgress);
}