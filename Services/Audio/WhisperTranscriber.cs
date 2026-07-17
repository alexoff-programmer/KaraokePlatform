using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Interfaces;
using KaraokePlatform.Services.Audio.Records;
using KaraokePlatform.Data;
using Microsoft.AspNetCore.Hosting;

namespace KaraokePlatform.Services.Audio;

public class WhisperTranscriber
{
    private readonly IAudioProcessor _audioProcessor;
    private readonly ISpeechRecognizer _speechRecognizer;
    private readonly IWebHostEnvironment _environment;
    private readonly AppDbContext _context;
    private readonly Microsoft.Extensions.Logging.ILogger<WhisperTranscriber>? _logger;

    // На CPU разрешаем обрабатывать только ОДНУ песню в один момент времени,
    // чтобы параллельный запуск нескольких задач не парализовал хост-систему.
    private static readonly SemaphoreSlim _cpuLocker = new SemaphoreSlim(1, 1);

    public WhisperTranscriber(
        IAudioProcessor audioProcessor,
        ISpeechRecognizer speechRecognizer,
        IWebHostEnvironment environment,
        AppDbContext context,
        Microsoft.Extensions.Logging.ILogger<WhisperTranscriber>? logger = null)
    {
        _audioProcessor = audioProcessor;
        _speechRecognizer = speechRecognizer;
        _environment = environment;
        _context = context;
        _logger = logger;
    }

    public virtual async Task<List<List<WordTimeInfo>>> ProcessAudioToPhrasesAsync(
        Guid taskId,
        string mp3FilePath,
        string language,
        string quality,
        Action<int> onProgress)
    {
        _logger?.LogInformation("[Transcriber] Starting audio processing pipeline for task {TaskId}. File: {FilePath}", taskId, mp3FilePath);
        await _cpuLocker.WaitAsync();

        try
        {
            string outputFolder = Path.Combine(_environment.WebRootPath, "output");
            Directory.CreateDirectory(outputFolder);
            string whisperVavPath = Path.Combine(outputFolder, $"{taskId}_vocals.wav");
            string instrumentalWavPath = Path.Combine(outputFolder, $"{taskId}_instrumental.wav");

            // 1. Разделяем трек на вокал и инструментал
            _logger?.LogInformation("[Transcriber] Step 1: Separating vocals and instrumental...");
            _audioProcessor.ConvertAndFilterMp3ToWav(mp3FilePath, whisperVavPath, instrumentalWavPath, quality, onProgress);
            onProgress.Invoke(25);
            _logger?.LogInformation("[Transcriber] Separation complete. Vocals WAV: {Path}", whisperVavPath);

            // 2. Запускаем Silero VAD для поиска отрезков с речью
            _logger?.LogInformation("[Transcriber] Step 2: Running Silero VAD...");
            var sileroModelPath = Path.Combine(_environment.ContentRootPath, "Models", "silero_vad.onnx");
            using var vadDetector = new SileroVadDetector(sileroModelPath, _logger);
            var vadIntervals = await vadDetector.GetSpeechIntervalsAsync(whisperVavPath);
            _logger?.LogInformation("[Transcriber] Silero VAD completed. Found {Count} speech intervals.", vadIntervals.Count);

            string debugDir = Path.Combine(_environment.WebRootPath, "output", $"debug_{taskId}");
            Directory.CreateDirectory(debugDir);
            File.WriteAllText(Path.Combine(debugDir, "1_vad_intervals.json"), 
                System.Text.Json.JsonSerializer.Serialize(vadIntervals, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            // 3. Читаем семплы вокала ОДИН раз для последующей нарезки в памяти
            _logger?.LogInformation("[Transcriber] Step 3: Loading raw samples from vocal track...");
            float[] allSamples;
            using (var fileStream = File.OpenRead(whisperVavPath))
            {
                var waveParser = new Whisper.net.Wave.WaveParser(fileStream);
                allSamples = await waveParser.GetAvgSamplesAsync();
            }
            _logger?.LogInformation("[Transcriber] Vocal samples loaded. Total samples: {Count}.", allSamples.Length);

            var allWords = new List<WordTimeInfo>();
            var task = await _context.KaraokeTasks.FindAsync(taskId);
            string? geminiApiKey = task?.GeminiApiKey;
            string? trackName = task != null ? Path.GetFileNameWithoutExtension(task.OriginalFileName) : null;
            string finalDetectedLanguage = language;

            // 4. Пофрагментный инференс через Whisper
            _logger?.LogInformation("[Transcriber] Step 4: Transcribing speech intervals in memory...");
            for (int i = 0; i < vadIntervals.Count; i++)
            {
                var interval = vadIntervals[i];
                TimeSpan startRef = interval.Start;
                
                // Нарезаем аудио-кусок в памяти для текущей VAD фразу
                float[] phraseSamples = _audioProcessor.SliceSamples(allSamples, ref startRef, interval.End);
                if (phraseSamples.Length == 0)
                {
                    _logger?.LogWarning("[Transcriber] Interval #{Index} ({Start} -> {End}) sliced to empty sample array. Skipping.", i + 1, interval.Start, interval.End);
                    continue;
                }

                _logger?.LogInformation("[Transcriber] Transcribing interval #{Index}/{Total} ({Start} -> {End}, duration: {Duration:F2}s, samples: {Samples})", 
                    i + 1, vadIntervals.Count, interval.Start, interval.End, (interval.End - interval.Start).TotalSeconds, phraseSamples.Length);

                // Вызываем Whisper для распознавания конкретного речевого фрагмента
                var (phraseWords, detectedLanguage) = await _speechRecognizer.TranscribeSamplesAsync(
                    phraseSamples,
                    language,
                    geminiApiKey,
                    trackName);

                if (i == 0) finalDetectedLanguage = detectedLanguage;

                // Корректируем относительные таймкоды слов Whisper, прибавляя оффсет начала VAD интервала
                foreach (var word in phraseWords)
                {
                    word.Start = word.Start.Add(interval.Start);
                    word.End = word.End.Add(interval.Start);
                }

                // Запускаем исправленный геометрический валидатор ИНДИВИДУАЛЬНО для каждого VAD-сегмента
                _logger?.LogInformation("[Transcriber] Running geometry validation for interval #{Index}. Words: {Count}", i + 1, phraseWords.Count);
                var correctedPhraseWords = KaraokeGeometryValidator.ValidateAndCorrect(phraseWords, interval.Start, interval.End);
                allWords.AddRange(correctedPhraseWords);

                // Обновляем прогресс транскрибации (от 25% до 50%)
                int phraseProgress = 25 + ((i + 1) * 25 / vadIntervals.Count);
                onProgress.Invoke(phraseProgress);
            }

            File.WriteAllText(Path.Combine(debugDir, "2_aligned_words_raw.json"), 
                System.Text.Json.JsonSerializer.Serialize(allWords, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            if (task != null && (string.IsNullOrEmpty(task.Language) || task.Language == "auto"))
            {
                task.Language = finalDetectedLanguage;
                await _context.SaveChangesAsync();
            }

            // 5. Группируем валидированные слова в результирующие строчки
            _logger?.LogInformation("[Transcriber] Step 5: Grouping {WordCount} words into phrases...", allWords.Count);
            var generator = new AssSubtitleGenerator();
            var phrases = generator.GroupWordsIntoPhrases(allWords);
            _logger?.LogInformation("[Transcriber] Grouping complete. Formatted {PhraseCount} subtitle phrases.", phrases.Count);

            File.WriteAllText(Path.Combine(debugDir, "4_final_corrected_phrases.json"), 
                System.Text.Json.JsonSerializer.Serialize(phrases, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            return phrases;
        }
        finally
        {
            _cpuLocker.Release();
        }
    }
}