using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Records;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Whisper.net.Wave;

namespace KaraokePlatform.Services.Audio;

public class SileroVadDetector : IDisposable
{
    private readonly string _modelPath;
    private InferenceSession? _session;

    public SileroVadDetector(string modelPath)
    {
        _modelPath = modelPath;
    }

    private async Task EnsureModelLoadedAsync()
    {
        if (_session != null) return;

        if (!File.Exists(_modelPath))
        {
            var directory = Path.GetDirectoryName(_modelPath);
            if (directory != null && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var downloadUrl = "https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx";
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(5);

            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var fileStream = new FileStream(_modelPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream);
        }

        var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
        options.AppendExecutionProvider_CPU();
        _session = new InferenceSession(_modelPath, options);
    }

    public async Task<List<AudioInterval>> GetSpeechIntervalsAsync(string wavPath, float threshold = 0.25f)
    {
        await EnsureModelLoadedAsync();

        float[] samples;
        using (var fileStream = File.OpenRead(wavPath))
        {
            var waveParser = new WaveParser(fileStream);
            samples = await waveParser.GetAvgSamplesAsync();
        }

        return DetectSpeech(samples, threshold);
    }

    private List<AudioInterval> DetectSpeech(float[] samples, float threshold)
    {
        if (_session == null) throw new InvalidOperationException("Model not loaded");

        int sampleRate = 16000;
        int chunkSize = 512; // 32ms frames
        int numChunks = samples.Length / chunkSize;

        // Determine VAD ONNX input structure
        bool useSplitState = _session.InputMetadata.ContainsKey("h");

        // Жестко фиксируем размерность под официальный спецификат Silero ONNX
        var hState = new float[2 * 1 * 64];
        var cState = new float[2 * 1 * 64];
        
        int stateLength = 64; // Default fallback for v4
        if (!useSplitState && _session.InputMetadata.TryGetValue("state", out var stateMeta))
        {
            if (stateMeta.Dimensions.Length >= 3 && stateMeta.Dimensions[2] > 0)
            {
                stateLength = stateMeta.Dimensions[2];
            }
            else
            {
                stateLength = 128; // Fallback for v5/dynamic
            }
        }
        
        var combinedState = new float[2 * 1 * stateLength]; 

        var chunk = new float[chunkSize];
        var inputTensor = new DenseTensor<float>(chunk, new[] { 1, chunkSize });
        var srTensor = new DenseTensor<long>(new[] { (long)sampleRate }, Array.Empty<int>());

        var rawIntervals = new List<AudioInterval>();
        bool inSpeech = false;
        double speechStart = 0;

        for (int i = 0; i < numChunks; i++)
        {
            Array.Copy(samples, i * chunkSize, chunk, 0, chunkSize);

            // Prepare inputs
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor),
                NamedOnnxValue.CreateFromTensor("sr", srTensor)
            };

            if (useSplitState)
            {
                var hTensor = new DenseTensor<float>(hState, new[] { 2, 1, 64 });
                var cTensor = new DenseTensor<float>(cState, new[] { 2, 1, 64 });
                inputs.Add(NamedOnnxValue.CreateFromTensor("h", hTensor));
                inputs.Add(NamedOnnxValue.CreateFromTensor("c", cTensor));
            }
            else
            {
                var stateTensor = new DenseTensor<float>(combinedState, new[] { 2, 1, stateLength });
                inputs.Add(NamedOnnxValue.CreateFromTensor("state", stateTensor));
            }

            using var results = _session.Run(inputs);

            // Fetch probability output
            var outputTensor = results.First(o => o.Name == "output").AsTensor<float>();
            float prob = outputTensor[0, 0];

            // Update states
            if (useSplitState)
            {
                var hnTensor = results.First(o => o.Name == "hn").AsTensor<float>();
                var cnTensor = results.First(o => o.Name == "cn").AsTensor<float>();
                
                int idx = 0;
                foreach (var val in hnTensor) hState[idx++] = val;
                idx = 0;
                foreach (var val in cnTensor) cState[idx++] = val;
            }
            else
            {
                var stateNTensor = results.First(o => o.Name == "stateN" || o.Name == "output_state" || o.Name == "state").AsTensor<float>();
                int idx = 0;
                foreach (var val in stateNTensor) combinedState[idx++] = val;
            }

            double currentTime = (double)i * chunkSize / sampleRate;

            if (prob >= threshold)
            {
                if (!inSpeech)
                {
                    inSpeech = true;
                    speechStart = currentTime;
                }
            }
            else
            {
                if (inSpeech)
                {
                    inSpeech = false;
                    double speechEnd = currentTime;
                    rawIntervals.Add(new AudioInterval(TimeSpan.FromSeconds(speechStart), TimeSpan.FromSeconds(speechEnd)));
                }
            }
        }

        if (inSpeech)
        {
            double speechEnd = (double)samples.Length / sampleRate;
            rawIntervals.Add(new AudioInterval(TimeSpan.FromSeconds(speechStart), TimeSpan.FromSeconds(speechEnd)));
        }

        return MergeAndFilterIntervals(rawIntervals);
    }

    private List<AudioInterval> MergeAndFilterIntervals(
        List<AudioInterval> rawIntervals, 
        double minSpeechDurationSec = 0.8, 
        double maxSilenceMergeSec = 0.7, 
        double maxSpeechDurationSec = 10.0,
        double speechPadSec = 0.25)
    {
        if (rawIntervals.Count == 0) return rawIntervals;

        var merged = new List<AudioInterval>();
        var current = rawIntervals[0];

        for (int i = 1; i < rawIntervals.Count; i++)
        {
            var next = rawIntervals[i];
            double gap = (next.Start - current.End).TotalSeconds;
            double prospectiveDuration = (next.End - current.Start).TotalSeconds;

            // Объединяем, если пауза мала И результирующий сегмент не превышает максимальную длину
            if (gap <= maxSilenceMergeSec && prospectiveDuration <= maxSpeechDurationSec)
            {
                current = current with { End = next.End };
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);

        // Фильтруем по минимальной длине и добавляем Speech Pad (амортизацию)
        var result = new List<AudioInterval>();
        foreach (var item in merged)
        {
            var duration = (item.End - item.Start).TotalSeconds;
            if (duration >= minSpeechDurationSec)
            {
                var paddedStart = item.Start - TimeSpan.FromSeconds(speechPadSec);
                if (paddedStart < TimeSpan.Zero) paddedStart = TimeSpan.Zero;

                var paddedEnd = item.End + TimeSpan.FromSeconds(speechPadSec);

                result.Add(new AudioInterval(paddedStart, paddedEnd));
            }
        }

        return result;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
