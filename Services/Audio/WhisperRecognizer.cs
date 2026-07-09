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

    public async Task<List<WordTimeInfo>> TranscribeAndMergeTokensAsync(
        string wavPath,
        string language,
        Action<int> onProgress,
        string? geminiApiKey = null,
        string? trackName = null)
    {
        if (!File.Exists(wavPath))
            throw new FileNotFoundException("Аудиофайл для транскрибации не найден", wavPath);

        onProgress?.Invoke(10); // Начало

        // Загружаем фабрику модели
        var factory = GetFactory();
        
        // Создаем процессор Whisper.net
        // WithConditionOnPreviousText(false) спасает от затыкания на рэпе!
        using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithNoContext()
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

        // Запускаем распознавание сегментов Whisper.net
        using var wavStream = File.OpenRead(wavPath);
        
        var segments = new List<(string Text, TimeSpan Start, TimeSpan End)>();
        await foreach (var segment in processor.ProcessAsync(wavStream))
        {
            if (!string.IsNullOrWhiteSpace(segment.Text))
            {
                segments.Add((segment.Text, segment.Start, segment.End));
            }
        }

        // Apply Gemini lyrics correction if API key is provided
        if (!string.IsNullOrEmpty(geminiApiKey))
        {
            segments = await ImproveSegmentsWithGeminiAsync(segments, geminiApiKey, trackName);
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