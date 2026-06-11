using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KaraokePlatform.Services.Audio.Interfaces
{
    public interface IAudioSilenceAnalyzer
    {
        /// <summary>
        /// Сканирует аудиофайл и возвращает интервалы, содержащие полезный аудиосигнал (вокал).
        /// </summary>
        List<(TimeSpan Start, TimeSpan End)> GetVocalIntervals(string wavPath, double thresholdDb = -42.0, double minSilenceDurationMs = 250);
    }
}