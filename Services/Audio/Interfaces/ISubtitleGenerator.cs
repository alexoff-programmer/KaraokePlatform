using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Records;

namespace KaraokePlatform.Services.Audio.Interfaces
{
    public interface ISubtitleGenerator
    {
        string GenerateKaraokeMarkup(List<WordTimeInfo> words);
    }
}