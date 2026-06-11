using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Interfaces;

namespace KaraokePlatform.Services.Audio
{
    public class AudioSilenceAnalyzer : IAudioSilenceAnalyzer
    {

        public List<(TimeSpan Start, TimeSpan End)> GetVocalIntervals(string wavPath, double thresholdDb = -42.0, double minSilenceDurationMs = 250)
        {
            var vocalIntervals = new List<(TimeSpan Start, TimeSpan End)>();

            // Передаем кодек "s16le" и выходной файл "-" (дефис означает вывод прямо в Stream Stdout)
            var arguments = $"-i \"{wavPath}\" -f s16le -ac 1 -ar 16000 -";

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new Exception("Не удалось запустить FFmpeg для спектрального анализа тишины.");
            }

            using var stdoutStream = process.StandardOutput.BaseStream;

            // Буфер на 20 мс звука: 16000 Гц * 0.020 сек * 2 байта на сэмпл = 640 байт
            const int bytesPerWindow = 640;
            byte[] buffer = new byte[bytesPerWindow];

            bool isInsideVocal = false;
            TimeSpan vocalStart = TimeSpan.Zero;
            TimeSpan lastSoundTime = TimeSpan.Zero;
            TimeSpan currentTime = TimeSpan.Zero;

            var windowDuration = TimeSpan.FromMilliseconds(20);

            int bytesRead;
            // Читаем поток блоками строго по 640 байт
            while ((bytesRead = ReadExactly(stdoutStream, buffer, buffer.Length)) > 0)
            {
                currentTime += windowDuration;

                double rms = CalculateWindowRms(buffer, bytesRead);
                double db = 20 * Math.Log10(rms);

                if (double.IsInfinity(db) || double.IsNaN(db)) db = -100.0;

                if (db >= thresholdDb)
                {
                    lastSoundTime = currentTime;
                    if (!isInsideVocal)
                    {
                        vocalStart = currentTime - TimeSpan.FromMilliseconds(20);
                        if (vocalStart < TimeSpan.Zero) vocalStart = TimeSpan.Zero;
                        isInsideVocal = true;
                    }
                }
                else
                {
                    if (isInsideVocal && (currentTime - lastSoundTime).TotalMilliseconds > minSilenceDurationMs)
                    {
                        vocalIntervals.Add((vocalStart, lastSoundTime + TimeSpan.FromMilliseconds(50)));
                        isInsideVocal = false;
                    }
                }
            }

            process.WaitForExit();

            if (isInsideVocal)
            {
                vocalIntervals.Add((vocalStart, currentTime));
            }

            return MergeCloseIntervals(vocalIntervals, TimeSpan.FromMilliseconds(200));
        }

        /// <summary>
        /// Потокобезопасное чтение фиксированного количества байт (так как поток Stdout может дробиться в ОС Linux)
        /// </summary>
        private int ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, totalRead, count - totalRead);
                if (read == 0) return totalRead;
                totalRead += read;
            }
            return totalRead;
        }

        private double CalculateWindowRms(byte[] buffer, int length)
        {
            double sum = 0;
            int sampleCount = 0;

            for (int i = 0; i < length; i += 2)
            {
                if (i + 1 >= length) break;

                // Собираем 16-битный сэмпл (Little Endian)
                short sample = (short)((buffer[i + 1] << 8) | buffer[i]);
                double normalized = sample / 32768.0;
                sum += normalized * normalized;
                sampleCount++;
            }

            return sampleCount > 0 ? Math.Sqrt(sum / sampleCount) : 0;
        }

        private List<(TimeSpan Start, TimeSpan End)> MergeCloseIntervals(List<(TimeSpan Start, TimeSpan End)> intervals, TimeSpan maxGap)
        {
            if (intervals.Count <= 1) return intervals;

            var merged = new List<(TimeSpan Start, TimeSpan End)> { intervals[0] };

            for (int i = 1; i < intervals.Count; i++)
            {
                var current = intervals[i];
                var previous = merged[^1];

                if (current.Start - previous.End <= maxGap)
                {
                    merged[^1] = (previous.Start, current.End);
                }
                else
                {
                    merged.Add(current);
                }
            }

            return merged;
        }
    }
}