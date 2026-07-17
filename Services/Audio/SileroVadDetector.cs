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
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    public SileroVadDetector(string modelPath, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _modelPath = modelPath;
        _logger = logger;
    }

    private async Task EnsureModelLoadedAsync()
    {
        if (_session != null) return;

        if (!File.Exists(_modelPath))
        {
            _logger?.LogInformation("[VAD] Silero VAD model not found at {ModelPath}. Downloading...", _modelPath);
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
            _logger?.LogInformation("[VAD] Download completed successfully.");
        }

        _logger?.LogInformation("[VAD] Loading Silero VAD Inference Session from {ModelPath}...", _modelPath);
        var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
        options.AppendExecutionProvider_CPU();
        _session = new InferenceSession(_modelPath, options);

        foreach (var input in _session.InputMetadata)
        {
            _logger?.LogInformation("[VAD DEBUG] Model expected input: name={Name}, shape={Shape}, type={Type}", 
                input.Key, string.Join(",", input.Value.Dimensions), input.Value.ElementType);
        }

        _logger?.LogInformation("[VAD] Inference Session loaded successfully.");
    }

    public async Task<List<AudioInterval>> GetSpeechIntervalsAsync(string wavPath, float threshold = 0.25f)
    {
        _logger?.LogInformation("[VAD] Getting speech intervals for {WavPath} with threshold {Threshold}", wavPath, threshold);
        await EnsureModelLoadedAsync();

        float[] samples = ReadWavMono(wavPath);
        return DetectSpeech(samples, threshold);
    }

    private List<AudioInterval> DetectSpeech(float[] samples, float threshold)
    {
        if (_session == null) throw new InvalidOperationException("Model not loaded");

        int sampleRate = 16000;
        int chunkSize = 512; // 32ms frames
        int numChunks = samples.Length / chunkSize;

        float maxAmp = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float absVal = Math.Abs(samples[i]);
            if (absVal > maxAmp) maxAmp = absVal;
        }
        _logger?.LogInformation("[VAD] Starting raw speech detection on {SamplesLength} samples ({NumChunks} chunks). Max amplitude: {MaxAmp:F6}", samples.Length, numChunks, maxAmp);

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
        
        _logger?.LogInformation("[VAD] Model metadata: splitState={UseSplitState}, stateLength={StateLength}", useSplitState, stateLength);
        var combinedState = new float[2 * 1 * stateLength]; 

        var srTensor = new DenseTensor<long>(new[] { (long)sampleRate }, new[] { 1 });
        var rawIntervals = new List<AudioInterval>();
        bool inSpeech = false;
        double speechStart = 0;

        int contextSize = 64;
        var context = new float[contextSize];

        float maxProb = 0f;
        for (int i = 0; i < numChunks; i++)
        {
            var inputBuffer = new float[contextSize + chunkSize];
            Array.Copy(context, 0, inputBuffer, 0, contextSize);
            Array.Copy(samples, i * chunkSize, inputBuffer, contextSize, chunkSize);
            
            var inputTensor = new DenseTensor<float>(inputBuffer, new[] { 1, contextSize + chunkSize });

            if (i == 0 || i == 100 || i == 200)
            {
                _logger?.LogInformation("[VAD DEBUG] Chunk #{Index} first 10 input samples (incl. context): {Vals}", 
                    i, string.Join(", ", inputBuffer.Take(10).Select(v => v.ToString("F6"))));
            }

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
                if (i < 5)
                {
                    _logger?.LogInformation("[VAD DEBUG] Chunk #{Index} state before run: {Vals}", 
                        i, string.Join(", ", combinedState.Take(5).Select(v => v.ToString("F4"))));
                }
                var stateTensor = new DenseTensor<float>(combinedState, new[] { 2, 1, stateLength });
                inputs.Add(NamedOnnxValue.CreateFromTensor("state", stateTensor));
            }

            using var results = _session.Run(inputs);

            // Log outputs on first iteration for diagnostics
            if (i == 0)
            {
                foreach (var res in results)
                {
                    _logger?.LogInformation("[VAD DEBUG] Model run output: {Name}", res.Name);
                }
            }

            // Fetch probability output
            var outputTensor = results.First(o => o.Name == "output").AsTensor<float>();
            float prob = outputTensor[0, 0];
            if (i % 50 == 0)
            {
                float chunkMaxAmp = inputBuffer.Skip(contextSize).Max(Math.Abs);
                _logger?.LogInformation("[VAD DEBUG] Chunk #{Index}: prob={Prob:F4}, maxAmp={MaxAmp:F4}", 
                    i, prob, chunkMaxAmp);
            }
            if (prob > maxProb) maxProb = prob;

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

                if (i < 5)
                {
                    int nonZeroCount = combinedState.Count(v => Math.Abs(v) > 1e-5f);
                    _logger?.LogInformation("[VAD DEBUG] Chunk #{Index} state after run: {Vals}. Non-zero elements: {NonZero} / {Total}", 
                        i, string.Join(", ", combinedState.Take(5).Select(v => v.ToString("F4"))), nonZeroCount, combinedState.Length);
                }
            }

            // Update context
            Array.Copy(inputBuffer, inputBuffer.Length - contextSize, context, 0, contextSize);

            double currentTime = (double)i * chunkSize / sampleRate;

            if (prob >= threshold)
            {
                if (!inSpeech)
                {
                    inSpeech = true;
                    speechStart = currentTime;
                    _logger?.LogDebug("[VAD] Speech onset detected at {SpeechStart:F3}s (prob: {Prob:F4})", speechStart, prob);
                }
            }
            else
            {
                if (inSpeech)
                {
                    inSpeech = false;
                    double speechEnd = currentTime;
                    _logger?.LogDebug("[VAD] Speech offset detected at {SpeechEnd:F3}s (prob: {Prob:F4}, duration: {Duration:F3}s)", speechEnd, prob, speechEnd - speechStart);
                    rawIntervals.Add(new AudioInterval(TimeSpan.FromSeconds(speechStart), TimeSpan.FromSeconds(speechEnd)));
                }
            }
        }

        if (inSpeech)
        {
            double speechEnd = (double)samples.Length / sampleRate;
            _logger?.LogDebug("[VAD] File end reached. Closing final speech segment at {SpeechEnd:F3}s (duration: {Duration:F3}s)", speechEnd, speechEnd - speechStart);
            rawIntervals.Add(new AudioInterval(TimeSpan.FromSeconds(speechStart), TimeSpan.FromSeconds(speechEnd)));
        }

        _logger?.LogInformation("[VAD] Raw speech detection complete. Found {Count} initial speech segments. Max prob: {MaxProb:F4}", rawIntervals.Count, maxProb);
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

        _logger?.LogInformation("[VAD] Final processed intervals after merge/filter/padding (minSpeechDurationSec={MinSpeechDurationSec}, maxSilenceMergeSec={MaxSilenceMergeSec}, speechPadSec={SpeechPadSec}): {Count}", 
            minSpeechDurationSec, maxSilenceMergeSec, speechPadSec, result.Count);

        for (int i = 0; i < result.Count; i++)
        {
            var interval = result[i];
            _logger?.LogInformation("[VAD] Interval #{Index}: {Start:hh\\:mm\\:ss\\.fff} -> {End:hh\\:mm\\:ss\\.fff} (duration: {Duration:F2}s)", 
                i + 1, interval.Start, interval.End, (interval.End - interval.Start).TotalSeconds);
        }

        return result;
    }

    private static float[] ReadWavMono(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        using var reader = new BinaryReader(fs);

        // Read RIFF header
        string signature = new string(reader.ReadChars(4));
        if (signature != "RIFF") throw new InvalidDataException("Not a RIFF file");

        reader.ReadInt32(); // File size

        string format = new string(reader.ReadChars(4));
        if (format != "WAVE") throw new InvalidDataException("Not a WAVE file");

        // Find 'fmt ' and 'data' chunks
        while (fs.Position < fs.Length)
        {
            string chunkId = new string(reader.ReadChars(4));
            int chunkSize = reader.ReadInt32();
            if (chunkId == "fmt ")
            {
                int audioFormat = reader.ReadInt16();
                int numChannels = reader.ReadInt16();
                int sampleRate = reader.ReadInt32();
                int byteRate = reader.ReadInt32();
                int blockAlign = reader.ReadInt16();
                int bitsPerSample = reader.ReadInt16();

                if (audioFormat != 1) // PCM
                    throw new NotSupportedException("Only PCM format is supported");
                if (numChannels != 1)
                    throw new NotSupportedException("Only mono format is supported");
                if (sampleRate != 16000)
                    throw new NotSupportedException("Only 16kHz sample rate is supported");
                if (bitsPerSample != 16)
                    throw new NotSupportedException("Only 16-bit sample depth is supported");

                // Skip extra format bytes if any
                if (chunkSize > 16)
                    fs.Seek(chunkSize - 16, SeekOrigin.Current);
            }
            else if (chunkId == "data")
            {
                int sampleCount = chunkSize / 2;
                var samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample16 = reader.ReadInt16();
                    samples[i] = sample16 / 32768.0f;
                }
                return samples;
            }
            else
            {
                fs.Seek(chunkSize, SeekOrigin.Current);
            }
        }
        throw new InvalidDataException("Data chunk not found");
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
