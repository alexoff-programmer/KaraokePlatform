using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Interfaces;
using KaraokePlatform.Services.Audio.Records;
using KaraokePlatform.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Text.Json;
using Whisper.net;
using Whisper.net.Wave;
using Whisper.net.Ggml;

namespace KaraokePlatform.Services.Audio;

public class WhisperRecognizer : ISpeechRecognizer
{
    private readonly string _modelPath;
    private readonly MmsForceAligner _forceAligner;
    private readonly IAudioProcessor _audioProcessor;
    private WhisperFactory? _factory;
    private readonly object _lock = new object();

    public WhisperRecognizer(
        IOptions<WhisperSettings> settings, 
        IWebHostEnvironment environment,
        MmsForceAligner forceAligner,
        IAudioProcessor audioProcessor)
    {
        var path = settings.Value.ModelPath;
        _modelPath = Path.IsPathRooted(path) ? path : Path.Combine(environment.ContentRootPath, path);
        _forceAligner = forceAligner;
        _audioProcessor = audioProcessor;
    }

    private WhisperFactory GetFactory()
    {
        if (_factory == null)
        {
            lock (_lock)
            {
                if (_factory == null)
                {
                    if (!File.Exists(_modelPath))
                    {
                        throw new FileNotFoundException($"Whisper модель не найдена по пути {_modelPath}");
                    }
                    _factory = WhisperFactory.FromPath(_modelPath);
                }
            }
        }
        return _factory;
    }

    private async Task EnsureModelDownloadedAsync()
    {
        if (File.Exists(_modelPath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(_modelPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var modelName = Path.GetFileName(_modelPath);
        var isMedium = modelName.Contains("medium");
        Console.WriteLine($"[Whisper] Model not found at {_modelPath}. Downloading {(isMedium ? "Medium" : "LargeV3 Turbo")} model...");

        var downloadUrl = isMedium 
            ? "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin"
            : "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin";
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(20);
            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(_modelPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fileStream);
            Console.WriteLine($"[Whisper] Model {(isMedium ? "Medium" : "LargeV3 Turbo")} successfully downloaded to {_modelPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Whisper] Failed to download model: {ex.Message}");
            throw;
        }
    }

    public async Task<List<WordTimeInfo>> TranscribeAndMergeTokensAsync(
        string wavPath,
        string language,
        Action<int> onProgress,
        string? geminiApiKey = null,
        string? trackName = null,
        List<AudioInterval>? vadIntervals = null)
    {
        if (!File.Exists(wavPath))
            throw new FileNotFoundException("Аудиофайл для транскрибации не найден", wavPath);

        onProgress?.Invoke(10); // Начало

        // Ensure the model is available on disk
        await EnsureModelDownloadedAsync();

        // Загружаем фабрику модели
        var factory = GetFactory();
        
        var processorBuilder = factory.CreateBuilder()
            .WithLanguage(language)
            .WithNoContext();


        using var processor = processorBuilder
            .WithEntropyThreshold(2.4f)
            .WithLogProbThreshold(-2.0f) // Смягчаем порог уверенности для песенного вокала
            .WithNoSpeechThreshold(0.8f) // Позволяем дольше обрабатывать тихие фрагменты внутри фраз
            .Build();

        onProgress?.Invoke(30);

        // Читаем все сэмплы из WAV-файла (требуется 16kHz, 16-bit, mono)
        float[] allSamples;
        using (var fileStream = File.OpenRead(wavPath))
        {
            var waveParser = new WaveParser(fileStream);
            allSamples = await waveParser.GetAvgSamplesAsync();
        }

        onProgress?.Invoke(50);

        var alignedWords = new List<WordTimeInfo>();
        var segments = new List<(string Text, TimeSpan Start, TimeSpan End)>();

        // ======================================================================
        // ЛОГИКА НАВЕДЕНИЯ WHISPER СТРОГО НА VAD-ОТРЕЗКИ
        // ======================================================================
        if (vadIntervals != null && vadIntervals.Count > 0)
        {
            Console.WriteLine($"[Whisper DEBUG] Starting audio transcription via VAD segments ({vadIntervals.Count} intervals)...");
            
            foreach (var interval in vadIntervals)
            {
                var segmentStart = interval.Start;
                var segmentEnd = interval.End;

                // Нарезаем сэмплы под конкретный кусок речи, определенный Silero VAD
                var chunkSamples = _audioProcessor.SliceSamples(allSamples, ref segmentStart, segmentEnd);
                if (chunkSamples.Length == 0) continue;

                // Отправляем в Whisper только этот кусочек данных
                await foreach (var segment in processor.ProcessAsync(chunkSamples))
                {
                    if (!string.IsNullOrWhiteSpace(segment.Text))
                    {
                        // ВАЖНО: Время сегмента возвращается относительно начала нарезки (с нуля).
                        // Прибавляем реальное смещение начала куска (segmentStart), чтобы восстановить абсолютное время в песне.
                        var absoluteStart = segmentStart.Add(segment.Start);
                        var absoluteEnd = segmentStart.Add(segment.End);

                        Console.WriteLine($"[Whisper DEBUG] VAD Segment Raw: [{absoluteStart} -> {absoluteEnd}] -> '{segment.Text}'");
                        segments.Add((segment.Text, absoluteStart, absoluteEnd));
                    }
                }
            }
        }
        else
        {
            // Резервный фолбэк: если VAD пустой или не передался, обрабатываем массив целиком
            Console.WriteLine($"[Whisper DEBUG] VAD intervals empty. Transcribing whole file via samples directly...");
            await foreach (var segment in processor.ProcessAsync(allSamples))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    Console.WriteLine($"[Whisper DEBUG] Raw Segment: [{segment.Start} -> {segment.End}] -> '{segment.Text}'");
                    segments.Add((segment.Text, segment.Start, segment.End));
                }
            }
        }
        // ======================================================================

        // Apply Gemini lyrics correction if API key is provided
        if (!string.IsNullOrEmpty(geminiApiKey))
        {
            Console.WriteLine($"[Whisper DEBUG] Applying Gemini correction. Segments count: {segments.Count}");
            segments = await ImproveSegmentsWithGeminiAsync(segments, geminiApiKey, trackName);
            foreach (var segment in segments)
            {
                Console.WriteLine($"[Whisper DEBUG] Post-Gemini Segment: [{segment.Start} -> {segment.End}] -> '{segment.Text}'");
            }
        }

        onProgress?.Invoke(75);

        // Производим пофразовое выравнивание с помощью ONNX Force Aligner
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];

            // 150ms leading padding and 350ms trailing padding (Audio Padding)
            var padLeft = TimeSpan.FromMilliseconds(150);
            var padRight = TimeSpan.FromMilliseconds(350);

            var paddedStart = segment.Start - padLeft;
            if (paddedStart < TimeSpan.Zero) paddedStart = TimeSpan.Zero;

            var paddedEnd = segment.End + padRight;
            double totalDurationSec = allSamples.Length / 16000.0;
            if (paddedEnd > TimeSpan.FromSeconds(totalDurationSec))
                paddedEnd = TimeSpan.FromSeconds(totalDurationSec);

            var segmentStart = paddedStart;
            var segmentEnd = paddedEnd;
            var segmentSamples = _audioProcessor.SliceSamples(allSamples, ref segmentStart, segmentEnd);

            if (segmentSamples.Length == 0) continue;

            // Выравниваем слова внутри сегмента с runwayFrames = 5
            var segmentWords = await _forceAligner.AlignAudioAsync(segmentSamples, segment.Text, language, 5);

            // Корректируем смещение времени относительно начала песни
            foreach (var w in segmentWords)
            {
                var cleanWordText = w.Text.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '`', '(', ')', '[', ']', '{', '}', '_', '*', '…', '-');
                if (string.IsNullOrWhiteSpace(cleanWordText)) continue;

                // Нормализуем ё -> е
                cleanWordText = cleanWordText.Replace("ё", "е").Replace("Ё", "Е");

                alignedWords.Add(new WordTimeInfo
                {
                    Text = cleanWordText,
                    Start = segmentStart.Add(w.Start),
                    End = segmentStart.Add(w.End)
                });
            }

            // Обновляем прогресс выравнивания
            int currentProgress = 75 + (i * 20 / segments.Count);
            onProgress?.Invoke(currentProgress);
        }

        onProgress?.Invoke(100);
        return alignedWords;
    }

    public async Task<List<(string Text, TimeSpan Start, TimeSpan End)>> ImproveSegmentsWithGeminiAsync(
        List<(string Text, TimeSpan Start, TimeSpan End)> segments,
        string apiKey,
        string? trackName = null)
    {
        if (segments.Count == 0 || string.IsNullOrWhiteSpace(apiKey)) return segments;

        try
        {
            var rawTexts = segments.Select(s => s.Text).ToList();
            var jsonInput = JsonSerializer.Serialize(rawTexts);

            var songTitle = string.IsNullOrEmpty(trackName) ? "Song" : trackName;
            var promptText = $"You are an expert lyrics editor. Adapt the subtitles for the track '{songTitle}'.\n" +
                             "CRITICAL: Do NOT translate the text to another language. Keep the original language of the lyrics intact. If the input text is in Russian, the output MUST be in Russian. If the input text is in English, the output MUST be in English.\n" +
                             "Only correct spelling errors, typos, punctuation, and formatting.\n" +
                             "If and only if there are obvious English words phonetically transliterated into Cyrillic (e.g. 'ай лав ю'), you may replace those specific words with standard English ('I love you'), but do NOT translate any other parts of the text.\n" +
                             "The text must be meaningful and correspond to the original song context.\n" +
                             "Keep the exact same number of elements in the output array as the input array. " +
                             "Do not combine or split segments. " +
                             "Return the output strictly in JSON format as an object containing the key \"corrected_segments\", which is an array of strings. " +
                             "\n\nInput segments:\n" + jsonInput;

            var requestBody = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = promptText
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    responseMimeType = "application/json"
                }
            };

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            var requestJson = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");

            // Call Gemini 3.1 Flash-Lite (standard fast model with JSON mode support)
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent?key={apiKey}";
            
            var response = await httpClient.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                var errorMsg = await response.Content.ReadAsStringAsync();
                throw new Exception($"Gemini API returned status code {response.StatusCode}. Details: {errorMsg}");
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            
            using var doc = JsonDocument.Parse(responseJson);
            var candidates = doc.RootElement.GetProperty("candidates");
            if (candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
            {
                var textResponse = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                if (!string.IsNullOrEmpty(textResponse))
                {
                    using var innerDoc = JsonDocument.Parse(textResponse);
                    var correctedEl = innerDoc.RootElement.GetProperty("corrected_segments");
                    if (correctedEl.ValueKind == JsonValueKind.Array)
                    {
                        var correctedTexts = new List<string>();
                        foreach (var el in correctedEl.EnumerateArray())
                        {
                            correctedTexts.Add(el.GetString() ?? string.Empty);
                        }

                        if (correctedTexts.Count == segments.Count)
                        {
                            var result = new List<(string Text, TimeSpan Start, TimeSpan End)>();
                            for (int i = 0; i < segments.Count; i++)
                            {
                                result.Add((correctedTexts[i], segments[i].Start, segments[i].End));
                            }
                            return result;
                        }
                        else
                        {
                            throw new Exception($"Gemini returned a different number of segments: expected {segments.Count}, got {correctedTexts.Count}");
                        }
                    }
                }
            }
            throw new Exception("Unexpected JSON structure in Gemini response");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Gemini Lyrics Correction Error] {ex.Message}. Falling back to raw Whisper segments.");
            return segments;
        }
    }
}