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

public class WhisperRecognizer : ISpeechRecognizer, IDisposable
{
    private readonly string _modelPath;
    private WhisperFactory? _factory;
    private readonly object _lock = new object();
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    private readonly Microsoft.Extensions.Logging.ILogger<WhisperRecognizer>? _logger;

    public WhisperRecognizer(
        IOptions<WhisperSettings> settings,
        IWebHostEnvironment environment,
        Microsoft.Extensions.Logging.ILogger<WhisperRecognizer>? logger = null)
    {
        var path = settings.Value.ModelPath;
        _modelPath = Path.IsPathRooted(path) ? path : Path.Combine(environment.ContentRootPath, path);
        _logger = logger;
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
            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
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

    public string DetectAudioLanguage(float[] allSamples)
    {
        try
        {
            var factory = GetFactory();
            using var processor = factory.CreateBuilder()
                .WithLanguageDetection()
                .WithSingleSegment()
                .Build();

            // Берем первые 30 секунд (480 000 сэмплов при 16кГц)
            int samplesToTake = Math.Min(allSamples.Length, 480000);
            var detectSamples = new float[samplesToTake];
            Array.Copy(allSamples, 0, detectSamples, 0, samplesToTake);

            var detectedLang = processor.DetectLanguage(detectSamples);
            Console.WriteLine($"[Whisper DetectAudioLanguage] Detected language code: '{detectedLang}'");
            return string.IsNullOrEmpty(detectedLang) ? "en" : detectedLang;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Whisper DetectAudioLanguage] Error: {ex.Message}. Fallback to 'ru'.");
            return "ru"; // Дефолтный фолбек
        }
    }

    public async Task<(List<WordTimeInfo> Words, string Language)> TranscribeAndMergeTokensAsync(
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

        // Читаем все сэмплы из WAV-файла (требуется 16kHz, 16-bit, mono)
        float[] allSamples;
        using (var fileStream = File.OpenRead(wavPath))
        {
            var waveParser = new WaveParser(fileStream);
            allSamples = await waveParser.GetAvgSamplesAsync();
        }

        string finalLanguage = language;
        if (language.ToLower() == "auto")
        {
            finalLanguage = DetectAudioLanguage(allSamples);
            Console.WriteLine($"[Whisper DEBUG] Автоопределение языка успешно: '{finalLanguage}'");
        }

        // Загружаем фабрику модели
        var factory = GetFactory();

        var processorBuilder = factory.CreateBuilder()
            .WithLanguage(finalLanguage)
            .WithNoContext();

        processorBuilder.WithBeamSearchSamplingStrategy(beam =>
        {
            beam.WithBeamSize(5);
        });

        using var processor = processorBuilder
            .WithTemperature(0.0f)
            .WithTokenTimestamps()       // ВКЛЮЧАЕМ пословные таймкоды (UseTokenTimestamps = true)
            .WithTokenTimestampsThreshold(0.01f)    // Порог вероятности по умолчанию (можно крутить для точности)
            .SplitOnWord()
            .Build();

        onProgress?.Invoke(30);

        var alignedWords = new List<WordTimeInfo>();

        if (vadIntervals != null && vadIntervals.Count > 0)
        {
            Console.WriteLine($"[Whisper DEBUG] Running transcription on {vadIntervals.Count} VAD intervals.");
            for (int i = 0; i < vadIntervals.Count; i++)
            {
                var interval = vadIntervals[i];
                int startSample = (int)(interval.Start.TotalSeconds * 16000.0);
                int endSample = (int)(interval.End.TotalSeconds * 16000.0);

                if (startSample < 0) startSample = 0;
                if (endSample > allSamples.Length) endSample = allSamples.Length;
                if (startSample >= endSample) continue;

                int length = endSample - startSample;
                var slice = new float[length];
                Array.Copy(allSamples, startSample, slice, 0, length);

                await foreach (var segment in processor.ProcessAsync(slice))
                {
                    if (segment.Tokens != null)
                    {
                        foreach (var token in segment.Tokens)
                        {
                            var rawText = token.Text;
                            if (string.IsNullOrEmpty(rawText)) continue;

                            if (rawText.StartsWith("[_") || rawText.StartsWith("<|") || rawText.EndsWith("]"))
                                continue;

                            bool hasSpace = rawText.StartsWith(" ") || rawText.StartsWith("Ġ") || rawText.StartsWith(" ");
                            bool isContinuation = !hasSpace && alignedWords.Count > 0;

                            var cleanText = rawText.Trim();
                            if (string.IsNullOrEmpty(cleanText)) continue;

                            if (isContinuation)
                            {
                                var lastWord = alignedWords[alignedWords.Count - 1];
                                lastWord.Text += cleanText;
                                lastWord.End = interval.Start + TimeSpan.FromMilliseconds(token.End * 10);
                            }
                            else
                            {
                                alignedWords.Add(new WordTimeInfo
                                {
                                    Text = cleanText,
                                    Start = interval.Start + TimeSpan.FromMilliseconds(token.Start * 10),
                                    End = interval.Start + TimeSpan.FromMilliseconds(token.End * 10)
                                });
                            }
                        }
                    }
                }

                // Update progress incrementally based on VAD chunk processing
                int currentProgress = 30 + (i * 70 / vadIntervals.Count);
                onProgress?.Invoke(currentProgress);
            }
        }
        else
        {
            Console.WriteLine("[Whisper DEBUG] Running transcription on full audio (single pass).");
            await foreach (var segment in processor.ProcessAsync(allSamples))
            {
                if (segment.Tokens != null)
                {
                    foreach (var token in segment.Tokens)
                    {
                        var rawText = token.Text;
                        if (string.IsNullOrEmpty(rawText)) continue;

                        if (rawText.StartsWith("[_") || rawText.StartsWith("<|") || rawText.EndsWith("]"))
                            continue;

                        bool hasSpace = rawText.StartsWith(" ") || rawText.StartsWith("Ġ") || rawText.StartsWith(" ");
                        bool isContinuation = !hasSpace && alignedWords.Count > 0;

                        var cleanText = rawText.Trim();
                        if (string.IsNullOrEmpty(cleanText)) continue;

                        if (isContinuation)
                        {
                            var lastWord = alignedWords[alignedWords.Count - 1];
                            lastWord.Text += cleanText;
                            lastWord.End = TimeSpan.FromMilliseconds(token.End * 10);
                        }
                        else
                        {
                            alignedWords.Add(new WordTimeInfo
                            {
                                Text = cleanText,
                                Start = TimeSpan.FromMilliseconds(token.Start * 10),
                                End = TimeSpan.FromMilliseconds(token.End * 10)
                            });
                        }
                    }
                }
            }
        }

        onProgress?.Invoke(100);
        return (alignedWords, finalLanguage);
    }

    public async Task<(List<WordTimeInfo> Words, string Language)> TranscribeSamplesAsync(
        float[] samples,
        string language,
        string? geminiApiKey = null,
        string? trackName = null)
    {
        _logger?.LogInformation("[Whisper] Starting transcription on sample slice (samples count: {Count}, language parameter: {Language})", samples.Length, language);
        await EnsureModelDownloadedAsync();

        string finalLanguage = language;
        if (language.ToLower() == "auto")
        {
            finalLanguage = DetectAudioLanguage(samples);
            _logger?.LogInformation("[Whisper] Auto-detected language: {Language}", finalLanguage);
        }

        var factory = GetFactory();

        var processorBuilder = factory.CreateBuilder()
            .WithLanguage(finalLanguage)
            .WithNoContext();

        processorBuilder.WithBeamSearchSamplingStrategy(beam =>
        {
            beam.WithBeamSize(5);
        });

        using var processor = processorBuilder
            .WithTemperature(0.0f)
            .WithTokenTimestamps()
            .WithTokenTimestampsThreshold(0.01f)
            .SplitOnWord()
            .Build();

        var alignedWords = new List<WordTimeInfo>();

        _logger?.LogInformation("[Whisper] Running model inference on slice...");
        await foreach (var segment in processor.ProcessAsync(samples))
        {
            if (segment.Tokens != null)
            {
                foreach (var token in segment.Tokens)
                {
                    var rawText = token.Text;
                    if (string.IsNullOrEmpty(rawText)) continue;

                    if (rawText.StartsWith("[_") || rawText.StartsWith("<|") || rawText.EndsWith("]"))
                        continue;

                    bool hasSpace = rawText.StartsWith(" ") || rawText.StartsWith("Ġ") || rawText.StartsWith(" ");
                    bool isContinuation = !hasSpace && alignedWords.Count > 0;

                    var cleanText = rawText.Trim();
                    if (string.IsNullOrEmpty(cleanText)) continue;

                    if (isContinuation)
                    {
                        var lastWord = alignedWords[alignedWords.Count - 1];
                        var oldText = lastWord.Text;
                        lastWord.Text += cleanText;
                        lastWord.End = TimeSpan.FromMilliseconds(token.End * 10);
                        _logger?.LogDebug("[Whisper] BPE merge: '{OldText}' + '{CleanText}' -> '{NewText}' (end extended to {End})", 
                            oldText, cleanText, lastWord.Text, lastWord.End);
                    }
                    else
                    {
                        var newWord = new WordTimeInfo
                        {
                            Text = cleanText,
                            Start = TimeSpan.FromMilliseconds(token.Start * 10),
                            End = TimeSpan.FromMilliseconds(token.End * 10)
                        };
                        alignedWords.Add(newWord);
                        _logger?.LogDebug("[Whisper] New word token: '{Text}' ({Start} -> {End})", newWord.Text, newWord.Start, newWord.End);
                    }
                }
            }
        }

        _logger?.LogInformation("[Whisper] Slice transcription complete. Words generated: {Count}", alignedWords.Count);
        return (alignedWords, finalLanguage);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _factory?.Dispose();
            _factory = null;
        }
    }
}