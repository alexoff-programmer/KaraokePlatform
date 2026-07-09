using System;
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

public class MmsForceAligner
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<MmsForceAligner> _logger;
    private readonly string _modelPath;
    private InferenceSession? _session;
    private readonly object _lock = new object();

    // ──────────────────────────────────────────────────────────────────
    //  BEAM SEARCH ПАРАМЕТРЫ
    // ──────────────────────────────────────────────────────────────────
    private const int BeamWidth = 8;                  // Лучших путей одновременно
    private const float BoostAlpha = 3.0f;            // Усиление вероятности целевого символа
    private const float BlankPenalty = 0.6f;          // Штраф за ПРОПУСК blank-токена между словами
    private const float TransitionPenalty = 0.8f;     // Штраф за переход к следующему символу

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
        _modelPath = Path.Combine(_environment.ContentRootPath, "Models", "mms-1b-all.onnx");
    }

    private async Task EnsureModelLoadedAsync()
    {
        if (_session != null) return;

        if (!File.Exists(_modelPath))
        {
            _logger.LogInformation("ONNX-модель выравнивания не найдена. Скачиваем Xenova/mms-1b-all quantized ONNX...");
            var directory = Path.GetDirectoryName(_modelPath);
            if (directory != null && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var downloadUrl = "https://huggingface.co/Xenova/mms-1b-all/resolve/main/onnx/model_quantized.onnx";
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(20);

            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var fileStream = new FileStream(_modelPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream);
            _logger.LogInformation("ONNX-модель выравнивания успешно скачана.");
        }

        lock (_lock)
        {
            if (_session == null)
            {
                var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
                options.AppendExecutionProvider_CPU();
                _session = new InferenceSession(_modelPath, options);
                _logger.LogInformation("Сессия ONNX Runtime для Force Aligner инициализирована.");
            }
        }
    }

    public async Task<List<WordTimeInfo>> AlignAudioAsync(float[] samples, string text, string language, int runwayFrames = 0)
    {
        await EnsureModelLoadedAsync();

        if (string.IsNullOrWhiteSpace(text) || samples.Length == 0)
            return new List<WordTimeInfo>();

        var vocabDict = LoadVocabulary(language);
        int blankId = vocabDict.TryGetValue('<', out int bId) ? bId : 0;  // CTC blank token

        // ── УЛУЧШЕНИЕ 2: Предобработка аудио ──────────────────────────
        var processedSamples = ApplyPreEmphasisAndRmsNorm(samples);

        // ── Подготовка токенов ────────────────────────────────────────
        var cleanText = text.ToLower().Replace("ё", "е");
        var tokens = new List<char>();
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
                    tokens.Add(c);
                    charToWordIndex.Add(words.Count - 1);
                }
                if (wIdx < wordParts.Length - 1)
                {
                    tokens.Add('|');
                    charToWordIndex.Add(-1);
                }
            }
        }

        if (tokens.Count == 0) return new List<WordTimeInfo>();

        int[] tokenIds = tokens.Select(c => vocabDict.TryGetValue(c, out int id) ? id : 3).ToArray();
        int N = tokenIds.Length;

        // ── ONNX-инференс ─────────────────────────────────────────────
        float[,] logProbs = RunInference(processedSamples);
        int T = logProbs.GetLength(0);

        // ── Внедрить VAD-маску на уровне логитов ─────────────────────
        int samplesPerFrame = 320;
        int numFrames = T;
        int vocabSize = logProbs.GetLength(1);

        bool[] isSilent = new bool[numFrames];
        for (int t = 0; t < numFrames; t++)
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

            // Quiet threshold is -25dB on raw un-normalized samples
            isSilent[t] = rmsDb < -25.0;
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

        // ── УЛУЧШЕНИЕ 3: Двунаправленное выравнивание ─────────────────
        _logger.LogDebug("[Aligner] Запуск прямого прохода Beam Search (T={T}, N={N})...", T, N);
        var (fwdPath, fwdScore) = BeamSearchAlign(logProbs, tokenIds, blankId, forward: true);

        _logger.LogDebug("[Aligner] Запуск обратного прохода Beam Search...");
        var (bwdPathRev, _) = BeamSearchAlign(ReverseLogProbs(logProbs), ReverseTokens(tokenIds), blankId, forward: false);
        var bwdPath = ReversePath(bwdPathRev, N);

        // Сшиваем два пути: прямой лучше находит начала слов, обратный — концы
        var mergedPath = MergePaths(fwdPath, bwdPath, N, T);

        // ── Определяем границы символов ──────────────────────────────
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

        // ── Группировка символов в слова ──────────────────────────────
        return BuildWordTimings(words, charStartFrames, charEndFrames, firstValidFrame, lastValidFrame);
    }

    // ─────────────────────────────────────────────────────────────────
    //  УЛУЧШЕНИЕ 1: CTC-Beam Search с языковым буcтингом
    // ─────────────────────────────────────────────────────────────────
    private (int[] path, float score) BeamSearchAlign(
        float[,] logProbs, int[] tokenIds, int blankId, bool forward)
    {
        int T = logProbs.GetLength(0);
        int N = tokenIds.Length;

        // Состояние луча: (state_index, score, backpointers)
        // state_index — текущая позиция в tokenIds (сколько токенов уже «обработано»)
        var beams = new List<(int state, float score, int[] path)>();
        var initialPath = Enumerable.Repeat(-1, T).ToArray();
        beams.Add((0, logProbs[0, tokenIds[0]], initialPath));

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
                    float advanceBoost = GetBoostedLogProb(logProbs, t, nextToken, state + 1, tokenIds);

                    // ── УЛУЧШЕНИЕ 4: Штраф за пропуск разделителя '|' ──
                    // Если перед переходом ожидается blank, но мы его не видим — штрафуем
                    float penalty = 0f;
                    if (tokens.Count > 0 && state < N - 1)
                    {
                        // Если следующий токен — разделитель '|', требуем blank между словами
                        int pipeId = vocabDictInstance != null && vocabDictInstance.TryGetValue('|', out int pId) ? pId : -1;
                        bool isWordBoundary = nextToken == pipeId;
                        if (isWordBoundary)
                        {
                            float blankProb = logProbs[t, blankId];
                            if (blankProb < -3.0f) // Нет явного blank — штраф
                                penalty = -BlankPenalty;
                        }
                    }

                    float advScore = prevScore + advanceBoost + penalty - TransitionPenalty;
                    TryUpdateBeam(newBeams, state + 1, advScore, prevPath, t, state + 1);
                }

                // Вариант 3: ПРОПУСКАЕМ blank-токен (blank pass)
                if (blankId >= 0)
                {
                    float blankScore = prevScore + logProbs[t, blankId];
                    TryUpdateBeam(newBeams, state, blankScore, prevPath, t, -1);
                }
            }

            // Обрезаем до BeamWidth лучших лучей по score
            beams = newBeams
                .OrderByDescending(kvp => kvp.Value.score)
                .Take(BeamWidth)
                .Select(kvp => (kvp.Key, kvp.Value.score, kvp.Value.path))
                .ToList();
        }

        if (beams.Count == 0)
            return (Enumerable.Repeat(-1, T).ToArray(), float.NegativeInfinity);

        var best = beams.OrderByDescending(b => b.score).First();
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

    /// <summary>
    /// Применяет усиление вероятности (boosting) для символов, которые принадлежат целевому слову.
    /// Это заставляет Beam Search «жестко следовать» тексту Whisper, игнорируя грязь в звуке.
    /// </summary>
    private float GetBoostedLogProb(float[,] logProbs, int t, int tokenId, int stateIdx, int[] tokenIds)
    {
        float baseProb = logProbs[t, tokenId];

        // Если текущий символ является частью ожидаемого слова — усиливаем вероятность
        if (stateIdx < tokenIds.Length && tokenId == tokenIds[stateIdx])
        {
            // Логарифмическое усиление: log(p * alpha) = log(p) + log(alpha)
            baseProb += (float)Math.Log(BoostAlpha);
        }

        return baseProb;
    }

    // ─────────────────────────────────────────────────────────────────
    //  УЛУЧШЕНИЕ 2: Предобработка аудио (Pre-emphasis + RMS Norm)
    // ─────────────────────────────────────────────────────────────────
    private float[] ApplyPreEmphasisAndRmsNorm(float[] samples)
    {
        if (samples.Length == 0) return samples;

        // Шаг А: Посегментная RMS-нормализация (скользящее окно ~2 секунды)
        var normalized = new float[samples.Length];
        float targetLinear = (float)Math.Pow(10.0, TargetRmsDb / 20.0);

        for (int start = 0; start < samples.Length; start += RmsWindowSamples)
        {
            int end = Math.Min(start + RmsWindowSamples, samples.Length);
            int len = end - start;

            // Считаем RMS окна
            double sumSq = 0;
            for (int i = start; i < end; i++) sumSq += samples[i] * samples[i];
            float rms = (float)Math.Sqrt(sumSq / len);

            // Вычисляем коэффициент усиления, ограничиваем чтобы не клиппить
            float gain = (rms > 1e-6f) ? Math.Clamp(targetLinear / rms, 0.1f, 10.0f) : 1.0f;

            for (int i = start; i < end; i++)
                normalized[i] = Math.Clamp(samples[i] * gain, -1.0f, 1.0f);
        }

        // Шаг Б: Формантный фильтр (Pre-emphasis) y[n] = x[n] - α * x[n-1]
        // Усиливает согласные (с, ц, т, к, ш) — критически важно для рэпа
        var preemphasized = new float[normalized.Length];
        preemphasized[0] = normalized[0];
        for (int i = 1; i < normalized.Length; i++)
        {
            preemphasized[i] = normalized[i] - PreEmphasisAlpha * normalized[i - 1];
        }

        return preemphasized;
    }

    // ─────────────────────────────────────────────────────────────────
    //  УЛУЧШЕНИЕ 3: Вспомогательные методы двунаправленного выравнивания
    // ─────────────────────────────────────────────────────────────────

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
                // Инвертируем индексы состояний (пересчёт из обратного прохода)
                reversed[T - 1 - t] = Math.Max(0, (N - 1) - path[t]);
            }
        }
        return reversed;
    }

    /// <summary>
    /// Сшивает прямой и обратный пути:
    /// - Для начала каждого символа берём прямой путь (он лучше находит START)
    /// - Для конца каждого символа берём обратный путь (он лучше находит END)
    /// </summary>
    private int[] MergePaths(int[] fwdPath, int[] bwdPath, int N, int T)
    {
        var merged = new int[T];

        for (int t = 0; t < T; t++)
        {
            int fwd = fwdPath[t];
            int bwd = bwdPath[t];

            if (fwd == -1 && bwd == -1)
            {
                merged[t] = -1;
            }
            else if (fwd == -1)
            {
                merged[t] = bwd;
            }
            else if (bwd == -1)
            {
                merged[t] = fwd;
            }
            else
            {
                // Для первой половины трека доверяем прямому пути больше,
                // для второй половины — обратному (двунаправленное «сшивание»)
                float weight = (float)t / T;
                merged[t] = (int)Math.Round(fwd * (1.0f - weight) + bwd * weight);
                merged[t] = Math.Clamp(merged[t], 0, N - 1);
            }
        }

        // Обеспечиваем монотонность (путь может только идти вперёд, пропуская blank -1)
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

    // ─────────────────────────────────────────────────────────────────
    //  ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
    // ─────────────────────────────────────────────────────────────────

    private float[,] RunInference(float[] samples)
    {
        float[,] logits;
        lock (_lock)
        {
            string inputName = _session!.InputMetadata.Keys.First();
            string outputName = _session.OutputMetadata.Keys.First();

            var inputTensor = new DenseTensor<float>(samples, new[] { 1, samples.Length });
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };

            using var results = _session.Run(inputs);
            var outputTensor = results.First().AsTensor<float>();

            int frameCount = outputTensor.Dimensions[1];
            int vocabSize = outputTensor.Dimensions[2];

            // Сразу вычисляем Log-Softmax
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

        // Find the first frame where path was not silent/blank (-1)
        int firstValidFrame = 0;
        for (int t = 0; t < T; t++)
        {
            if (path[t] != -1)
            {
                firstValidFrame = t;
                break;
            }
        }

        // Интерполяция для символов без фреймов
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
        List<string> words, int[] charStartFrames, int[] charEndFrames, int firstValidFrame, int lastValidFrame)
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

            var start = TimeSpan.FromMilliseconds(startFrame * 20);
            var end = TimeSpan.FromMilliseconds(endFrame * 20);

            if (end <= start) end = start.Add(TimeSpan.FromMilliseconds(100));

            result.Add(new WordTimeInfo { Text = words[wIdx], Start = start, End = end });

            firstCharIdx += wordLen + 1; // +1 пропускаем разделитель '|'
        }

        return result;
    }

    // Workaround: экземпляр vocabDict для доступа в GetBoostedLogProb
    // (поскольку это private static-like, мы кэшируем при первом вызове AlignAudioAsync)
    private Dictionary<char, int>? vocabDictInstance;

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
                foreach (var kvp in rawVocab)
                    if (kvp.Key.Length == 1)
                        vocabDict[kvp.Key[0]] = kvp.Value;

            vocabDictInstance = vocabDict;
            return vocabDict;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось загрузить словарь из {Path}.", vocabPath);
            return new Dictionary<char, int>();
        }
    }

    // Поле tokens — нужно для доступа в методе BeamSearchAlign
    private List<char> tokens = new();
}
