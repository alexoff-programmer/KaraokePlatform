using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KaraokePlatform.Services.Audio.Interfaces
{
    public interface IAudioProcessor
    {
        void ConvertAndFilterMp3ToWav(string inputPath, string outputPath, Action<int>? onProgress = null);
    }
}