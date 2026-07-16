using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Records;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace KaraokePlatform.Services.Audio;

public class MmsForceAligner : IDisposable
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<MmsForceAligner> _logger;
    private readonly string _modelsDir;
    private readonly ConcurrentDictionary<string, InferenceSession> _sessions = new ConcurrentDictionary<string, InferenceSession>();
    private readonly object _lock = new object();
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };

    // ──────────────────────────────────────────────────────────────────
    //  BEAM SEARCH ПАРАМЕТРЫ
    // ──────────────────────────────────────────────────────────────────
    private const int BeamWidth = 32;                 // Лучших путей одновременно
    private const float BoostAlpha = 3.0f;            // Усиление вероятности целевого символа
    private const float BlankPenalty = 0.6f;          // Штраф за ПРОПУСК blank-токена между словами
    private const float TransitionPenalty = 0.2f;     // Штраф за переход к следующему символу

    // ──────────────────────────────────────────────────────────────────
    //  АУДИО-ПРЕДОБРАБОТКА
    // ──────────────────────────────────────────────────────────────────
    private const float PreEmphasisAlpha = 0.97f;     // Коэффициент формантного фильтра
    private const int RmsWindowSamples = 3200;        // 200ms микро-окно для RMS-нормализации при 16кГц
    private const float TargetRmsDb = -18.0f;         // Целевой уровень RMS в dBFS

    public MmsForceAligner(IWebHostEnvironment environment, ILogger<MmsForceAligner> logger)
    {
        _environment = environment;
        _logger = logger;
        _modelsDir = Path.Combine(_environment.ContentRootPath, "Models");
    }

    private async Task<InferenceSession> GetSessionAsync(string modelName)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(modelName, out var s))
                return s;
        }

        string modelPath = Path.Combine(_modelsDir, modelName);

        if (!File.Exists(modelPath))
        {
            _logger.LogInformation("ONNX-модель выравнивания {ModelName} не найдена. Скачиваем...", modelName);
            var directory = Path.GetDirectoryName(modelPath);
            if (directory != null && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string downloadUrl = modelName.Contains("russian")
                ? "https://modelscope.cn/api/v1/models/onnx-community/wav2vec2-large-xlsr-53-russian-ONNX/repo?Revision=master&FilePath=onnx/model_quantized.onnx"
                : "https://modelscope.cn/api/v1/models/Xenova/mms-1b-all/repo?Revision=master&FilePath=onnx/model_quantized.onnx";

            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var fileStream = new FileStream(modelPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream);
            _logger.LogInformation("ONNX-модель {ModelName} успешно скачана.", modelName);
        }

        lock (_lock)
        {
            if (_sessions.TryGetValue(modelName, out var s))
                return s;

            using var options = new Microsoft.ML.OnnxRuntime.SessionOptions();

            try
            {
                // 0 — это обычно индекс основной видеокарты (в вашем случае Radeon 780M)
                options.AppendExecutionProvider_DML(0);
                _logger.LogInformation("Сессия ONNX Runtime для {ModelName} успешно переключена на GPU (DirectML).", modelName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Не удалось инициализировать DirectML GPU: {ex.Message}. Откат на CPU.");
                options.IntraOpNumThreads = 1;
                options.InterOpNumThreads = 1;
                options.AppendExecutionProvider_CPU();
            }

            var session = new InferenceSession(modelPath, options);
            _sessions[modelName] = session;
            return session;
        }
    }

    public async Task<List<WordTimeInfo>> AlignAudioAsync(
        float[] allSamples, 
        int sampleStart, 
        int sampleEnd, 
        string text, 
        string language, 
        int runwayFrames = 0,
        List<AudioInterval>? vadIntervals = null)
    {
        string modelName = language.ToLower() == "ru" 
            ? "wav2vec2-large-xlsr-53-russian.onnx" 
            : "mms-1b-all.onnx";

        var session = await GetSessionAsync(modelName);

        if (string.IsNullOrWhiteSpace(text) || allSamples == null || allSamples.Length == 0)
            return new List<WordTimeInfo>();

        int segmentLength = sampleEnd - sampleStart;
        if (segmentLength <= 0 || sampleStart < 0 || sampleStart >= allSamples.Length)
            return new List<WordTimeInfo>();
        if (sampleStart + segmentLength > allSamples.Length)
            segmentLength = allSamples.Length - sampleStart;

        var samples = new float[segmentLength];
        Array.Copy(allSamples, sampleStart, samples, 0, segmentLength);

        var vocabDict = LoadVocabulary(language);
        int blankId = vocabDict.TryGetValue('<', out int bId) ? bId : 0;  // CTC blank token

        // Предобработка аудио
        var processedSamples = ApplyPreEmphasisAndRmsNorm(samples);

        // Подготовка токенов (локальные переменные)
        var cleanText = text.ToLower().Replace("ё", "е");
        var localTokens = new List<char>();
        var charToWordIndex = new List<int>();
        var words = new List<string>();

        var wordParts = cleanText.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int wIdx = 0; wIdx < wordParts.Length; wIdx++)
        {
            var wordClean = new string(wordParts[wIdx].Where(c => vocabDict.ContainsKey(c)).ToArray());
            if (wordClean.Length > 0)
            {
                words.Add(wordClean);
                foreach (var c in wordClean)
                {
                    localTokens.Add(c);
                    charToWordIndex.Add(words.Count - 1);
                }
                if (wIdx < wordParts.Length - 1)
                {
                    localTokens.Add('|');
                    charToWordIndex.Add(-1);
                }
            }
        }

        if (localTokens.Count == 0) return new List<WordTimeInfo>();

        int[] tokenIds = localTokens.Select(c => vocabDict.TryGetValue(c, out int id) ? id : 3).ToArray();
        int N = tokenIds.Length;

        // ONNX-инференс
        float[,] logProbs = RunInference(processedSamples, session);
        int T = logProbs.GetLength(0);

        // Внедрить VAD-маску на уровне логитов
        int samplesPerFrame = 320;
        int numFrames = T;
        int vocabSize = logProbs.GetLength(1);

        bool[] isSilent = new bool[numFrames];
        for (int t = 0; t < numFrames; t++)
        {
            if (vadIntervals != null && vadIntervals.Count > 0)
            {
                // Вычисляем абсолютное время текущего фрейма в секундах
                double frameStartSec = (sampleStart + t * samplesPerFrame) / 16000.0;
                double frameEndSec = (sampleStart + Math.Min((t + 1) * samplesPerFrame, samples.Length)) / 16000.0;
                double frameMidSec = (frameStartSec + frameEndSec) / 2.0;

                // Проверяем, попадает ли средняя точка фрейма в любой из интервалов VAD (с запасом 150мс на границы)
                bool inSpeech = vadIntervals.Any(v => 
                    frameMidSec >= v.Start.TotalSeconds - 0.15 && 
                    frameMidSec <= v.End.TotalSeconds + 0.15
                );
                isSilent[t] = !inSpeech;
            }
            else
            {
                int frameStart = t * samplesPerFrame;
                int frameEnd = Math.Min(frameStart + samplesPerFrame, samples.Length);
                if (frameStart >= samples.Length)
                {
                    isSilent[t] = true;
                    continue;
                }

                double sumSq = 0;
                for (int i = frameStart; i < frameEnd; i++)
                {
                    sumSq += samples[i] * samples[i];
                }
                double rms = Math.Sqrt(sumSq / (frameEnd - frameStart));
                double rmsDb = 20 * Math.Log10(rms + 1e-10);

                isSilent[t] = rmsDb < -25.0;
            }
        }

        bool[] keepNormal = new bool[numFrames];
        for (int t = 0; t < numFrames; t++)
        {
            if (!isSilent[t])
            {
                int startRange = Math.Max(0, t - runwayFrames);
                int endRange = Math.Min(numFrames - 1, t + runwayFrames);
                for (int r = startRange; r <= endRange; r++)
                {
                    keepNormal[r] = true;
                }
            }
        }

        // Применяем маскирование: в тишине/шуме зануляем вероятности всех символов, оставляя только blank
        for (int t = 0; t < numFrames; t++)
        {
            if (!keepNormal[t])
            {
                for (int v = 0; v < vocabSize; v++)
                {
                    if (v == blankId)
                        logProbs[t, v] = 0.0f;
                    else
                        logProbs[t, v] = -100.0f;
                }
            }
        }

        // Прямой проход Beam Search (T={T}, N={N})
        _logger.LogDebug("[Aligner] Запуск Beam Search (T={T}, N={N})...", T, N);
        var (fwdPath, fwdScore) = BeamSearchAlign(logProbs, tokenIds, blankId, isSilent, localTokens, vocabDict);

        var mergedPath = fwdPath;

        // Определяем границы символов
        var (charStartFrames, charEndFrames) = ExtractCharFrames(mergedPath, N, T);

        int firstValidFrame = 0;
        for (int t = 0; t < T; t++)
        {
            if (mergedPath[t] != -1)
            {
                firstValidFrame = t;
                break;
            }
        }

        int lastValidFrame = T - 1;
        for (int t = T - 1; t >= 0; t--)
        {
            if (mergedPath[t] != -1)
            {
                lastValidFrame = t;
                break;
            }
        }

        // Группировка символов в слова
        return BuildWordTimings(words, charStartFrames, charEndFrames, firstValidFrame, lastValidFrame, sampleStart);
    }

    private (int[] path, float score) BeamSearchAlign(
        float[,] logProbs, int[] tokenIds, int blankId, bool[] isSilent, List<char> localTokens, Dictionary<char, int> vocabDict)
    {
        int T = logProbs.GetLength(0);
        int N = tokenIds.Length;

        int[] activeFramesUpTo = new int[T];
        int totalActiveFrames = 0;
        for (int t = 0; t < T; t++)
        {
            if (!isSilent[t])
            {
                totalActiveFrames++;
            }
            activeFramesUpTo[t] = totalActiveFrames;
        }

        var beams = new List<(int state, float score, int[] path)>();
        
        // Вариант А: Начинаем сразу с первого токена
        var initialPathToken = Enumerable.Repeat(-1, T).ToArray();
        initialPathToken[0] = 0;
        beams.Add((0, logProbs[0, tokenIds[0]], initialPathToken));

        // Вариант Б: Начинаем с blank (молчание)
        if (blankId >= 0)
        {
            var initialPathBlank = Enumerable.Repeat(-1, T).ToArray();
            initialPathBlank[0] = -1;
            beams.Add((0, logProbs[0, blankId], initialPathBlank));
        }

        for (int t = 1; t < T; t++)
        {
            var newBeams = new Dictionary<int, (float score, int[] path)>();

            foreach (var (state, prevScore, prevPath) in beams)
            {

                // Вариант 1: ОСТАЁМСЯ на текущем токене (stay)
                float stayScore = prevScore + GetBoostedLogProb(logProbs, t, tokenIds[state], state, tokenIds);
                TryUpdateBeam(newBeams, state, stayScore, prevPath, t, state);

                // Вариант 2: ПЕРЕХОДИМ на следующий токен (advance)
                if (state + 1 < N)
                {
                    int nextToken = tokenIds[state + 1];

                    bool isDuplicate = tokenIds[state] == nextToken;
                    bool prevWasBlank = prevPath[t - 1] == -1;

                    // Если мы на первом состоянии (state = 0), мы можем перейти на следующее состояние
                    // только если мы хотя бы один раз выделили первый токен (т.е. вышли из начального молчания)
                    bool canAdvance = true;
                    if (state == 0)
                    {
                        canAdvance = false;
                        for (int i = 0; i < t; i++)
                        {
                            if (prevPath[i] == 0)
                            {
                                canAdvance = true;
                                break;
                            }
                        }
                    }

                    if (canAdvance && (!isDuplicate || prevWasBlank))
                    {
                        float advanceBoost = GetBoostedLogProb(logProbs, t, nextToken, state + 1, tokenIds);

                        float penalty = 0f;
                        if (localTokens.Count > 0 && state < N - 1)
                        {
                            int pipeId = vocabDict.TryGetValue('|', out int pId) ? pId : -1;
                            bool isWordBoundary = nextToken == pipeId;
                            if (isWordBoundary)
                            {
                                float blankProb = logProbs[t, blankId];
                                if (blankProb < -3.0f)
                                    penalty = -BlankPenalty;
                            }
                        }

                        float advScore = prevScore + advanceBoost + penalty - TransitionPenalty;
                        TryUpdateBeam(newBeams, state + 1, advScore, prevPath, t, state + 1);
                    }
                }

                // Вариант 3: ПРОПУСКАЕМ blank-токен (blank pass)
                if (blankId >= 0)
                {
                    float blankScore = prevScore + logProbs[t, blankId];
                    TryUpdateBeam(newBeams, state, blankScore, prevPath, t, -1);
                }
            }

            if (newBeams.Count > 0)
            {
                beams = newBeams
                    .OrderByDescending(kvp => kvp.Value.score)
                    .Take(BeamWidth)
                    .Select(kvp => (kvp.Key, kvp.Value.score, kvp.Value.path))
                    .ToList();
            }
        }

        if (beams.Count == 0)
            return (Enumerable.Repeat(-1, T).ToArray(), float.NegativeInfinity);

        var best = beams.OrderByDescending(b => b.state).ThenByDescending(b => b.score).First();
        return (best.path, best.score);
    }

    private void TryUpdateBeam(
        Dictionary<int, (float score, int[] path)> beams,
        int state, float score, int[] prevPath, int t, int assignState)
    {
        var newPath = (int[])prevPath.Clone();
        newPath[t] = assignState;

        if (!beams.TryGetValue(state, out var existing) || existing.score < score)
        {
            beams[state] = (score, newPath);
        }
    }

    private float GetBoostedLogProb(float[,] logProbs, int t, int tokenId, int stateIdx, int[] tokenIds)
    {
        float baseProb = logProbs[t, tokenId];
        if (stateIdx < tokenIds.Length && tokenId == tokenIds[stateIdx])
        {
            baseProb += (float)Math.Log(BoostAlpha);
        }
        return baseProb;
    }

    private float[] ApplyPreEmphasisAndRmsNorm(float[] samples)
    {
        if (samples.Length == 0) return samples;

        var normalized = new float[samples.Length];
        float targetLinear = (float)Math.Pow(10.0, TargetRmsDb / 20.0);

        for (int start = 0; start < samples.Length; start += RmsWindowSamples)
        {
            int end = Math.Min(start + RmsWindowSamples, samples.Length);
            int len = end - start;

            double sumSq = 0;
            for (int i = start; i < end; i++) sumSq += samples[i] * samples[i];
            float rms = (float)Math.Sqrt(sumSq / len);

            float gain = (rms > 1e-6f) ? Math.Clamp(targetLinear / rms, 0.1f, 10.0f) : 1.0f;

            for (int i = start; i < end; i++)
                normalized[i] = Math.Clamp(samples[i] * gain, -1.0f, 1.0f);
        }

        var preemphasized = new float[normalized.Length];
        preemphasized[0] = normalized[0];
        for (int i = 1; i < normalized.Length; i++)
        {
            preemphasized[i] = normalized[i] - PreEmphasisAlpha * normalized[i - 1];
        }

        return preemphasized;
    }

    private float[,] ReverseLogProbs(float[,] logProbs)
    {
        int T = logProbs.GetLength(0);
        int V = logProbs.GetLength(1);
        var rev = new float[T, V];
        for (int t = 0; t < T; t++)
            for (int v = 0; v < V; v++)
                rev[t, v] = logProbs[T - 1 - t, v];
        return rev;
    }

    private int[] ReverseTokens(int[] tokens)
    {
        var rev = (int[])tokens.Clone();
        Array.Reverse(rev);
        return rev;
    }

    private int[] ReversePath(int[] path, int N)
    {
        int T = path.Length;
        var reversed = new int[T];
        for (int t = 0; t < T; t++)
        {
            if (path[t] == -1)
            {
                reversed[T - 1 - t] = -1;
            }
            else
            {
                reversed[T - 1 - t] = Math.Max(0, (N - 1) - path[t]);
            }
        }
        return reversed;
    }

    private int[] MergePaths(int[] fwdPath, int[] bwdPath, int N, int T)
    {
        var merged = new int[T];

        for (int t = 0; t < T; t++)
        {
            int fwd = fwdPath[t];
            int bwd = bwdPath[t];

            if (fwd == -1 && bwd == -1) merged[t] = -1;
            else if (fwd == -1) merged[t] = bwd;
            else if (bwd == -1) merged[t] = fwd;
            else
            {
                float weight = (float)t / T;
                merged[t] = (int)Math.Round(fwd * (1.0f - weight) + bwd * weight);
                merged[t] = Math.Clamp(merged[t], 0, N - 1);
            }
        }

        int lastState = 0;
        for (int t = 0; t < T; t++)
        {
            if (merged[t] != -1)
            {
                if (merged[t] < lastState)
                    merged[t] = lastState;
                lastState = merged[t];
            }
        }

        return merged;
    }

    private float[,] RunInference(float[] samples, InferenceSession session)
    {
        float[,] logits;
        lock (_lock) // Фикс: Потокобезопасная защита инференса ONNX-сессии
        {
            string inputName = session.InputMetadata.Keys.First();
            string outputName = session.OutputMetadata.Keys.First();

            var inputTensor = new DenseTensor<float>(samples, new[] { 1, samples.Length });
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };

            using var results = session.Run(inputs);
            var outputTensor = results.First().AsTensor<float>();

            int frameCount = outputTensor.Dimensions[1];
            int vocabSize = outputTensor.Dimensions[2];

            logits = new float[frameCount, vocabSize];
            for (int t = 0; t < frameCount; t++)
            {
                float maxLogit = float.MinValue;
                for (int v = 0; v < vocabSize; v++)
                    if (outputTensor[0, t, v] > maxLogit) maxLogit = outputTensor[0, t, v];

                float sumExp = 0f;
                for (int v = 0; v < vocabSize; v++)
                    sumExp += (float)Math.Exp(outputTensor[0, t, v] - maxLogit);

                float logSumExp = maxLogit + (float)Math.Log(sumExp);
                for (int v = 0; v < vocabSize; v++)
                    logits[t, v] = outputTensor[0, t, v] - logSumExp;
            }
        }
        return logits;
    }

    private (int[] startFrames, int[] endFrames) ExtractCharFrames(int[] path, int N, int T)
    {
        var charStartFrames = Enumerable.Repeat(-1, N).ToArray();
        var charEndFrames = Enumerable.Repeat(-1, N).ToArray();

        for (int t = 0; t < T; t++)
        {
            int s = path[t];
            if (s < 0 || s >= N) continue;
            if (charStartFrames[s] == -1) charStartFrames[s] = t;
            charEndFrames[s] = t;
        }

        int firstValidFrame = 0;
        for (int t = 0; t < T; t++)
        {
            if (path[t] != -1)
            {
                firstValidFrame = t;
                break;
            }
        }

        int lastValidFrame = firstValidFrame;
        for (int i = 0; i < N; i++)
        {
            if (charStartFrames[i] == -1)
            {
                charStartFrames[i] = lastValidFrame;
                charEndFrames[i] = lastValidFrame;
            }
            else
            {
                lastValidFrame = charEndFrames[i];
            }
        }

        return (charStartFrames, charEndFrames);
    }

    private List<WordTimeInfo> BuildWordTimings(
        List<string> words, int[] charStartFrames, int[] charEndFrames, int firstValidFrame, int lastValidFrame, int sampleStart)
    {
        var result = new List<WordTimeInfo>();
        int firstCharIdx = 0;

        for (int wIdx = 0; wIdx < words.Count; wIdx++)
        {
            int wordLen = words[wIdx].Length;
            int startFrame = charStartFrames[firstCharIdx];
            int endFrame = charEndFrames[firstCharIdx + wordLen - 1];

            if (wIdx == 0)
            {
                if (firstValidFrame >= 0 && firstValidFrame < endFrame)
                {
                    startFrame = firstValidFrame;
                }
            }

            if (wIdx == words.Count - 1)
            {
                if (lastValidFrame > startFrame)
                {
                    endFrame = lastValidFrame;
                }
            }

            int wStartSample = sampleStart + startFrame * 320;
            int wEndSample = sampleStart + (endFrame + 1) * 320;

            if (wEndSample <= wStartSample) wEndSample = wStartSample + 1600;

            result.Add(new WordTimeInfo
            {
                Text = words[wIdx],
                StartSample = wStartSample,
                EndSample = wEndSample
            });

            firstCharIdx += wordLen + 1;
        }

        return result;
    }

    private Dictionary<char, int> LoadVocabulary(string language)
    {
        var langCode = language.ToLower() == "en" ? "eng" : "rus";
        var vocabPath = Path.Combine(_environment.WebRootPath, "data", $"vocab_{langCode}.json");

        if (!File.Exists(vocabPath))
            vocabPath = Path.Combine(_environment.WebRootPath, "data", "vocab_rus.json");

        try
        {
            var json = File.ReadAllText(vocabPath);
            var rawVocab = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            var vocabDict = new Dictionary<char, int>();

            if (rawVocab != null)
            {
                foreach (var kvp in rawVocab)
                {
                    if (kvp.Key.Length == 1)
                    {
                        vocabDict[kvp.Key[0]] = kvp.Value;
                    }
                    else if (kvp.Key == "[PAD]" || kvp.Key == "<pad>")
                    {
                        vocabDict['<'] = kvp.Value;
                    }
                }
            }

            return vocabDict;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось загрузить словарь из {Path}.", vocabPath);
            return new Dictionary<char, int>();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var session in _sessions.Values)
            {
                session?.Dispose();
            }
            _sessions.Clear();
        }
    }
}