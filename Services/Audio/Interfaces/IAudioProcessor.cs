using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KaraokePlatform.Services.Audio.Interfaces
{
    public interface IAudioProcessor
    {
        void ConvertAndFilterMp3ToWav(string inputPath, string outputPath, string instrumentalOutputPath, string quality = "medium", Action<int>? onProgress = null);
        float[] SliceSamples(float[] samples, ref TimeSpan start, TimeSpan end, bool trimSilence = false);
    }
}