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

    // На CPU разрешаем обрабатывать только ОДНУ песню в один момент времени,
    // чтобы параллельный запуск нескольких задач не парализовал хост-систему.
    private static readonly SemaphoreSlim _cpuLocker = new SemaphoreSlim(1, 1);

    public WhisperTranscriber(
        IAudioProcessor audioProcessor,
        ISpeechRecognizer speechRecognizer,
        IWebHostEnvironment environment,
        AppDbContext context)
    {
        _audioProcessor = audioProcessor;
        _speechRecognizer = speechRecognizer;
        _environment = environment;
        _context = context;
    }

    public virtual async Task<List<List<WordTimeInfo>>> ProcessAudioToPhrasesAsync(
        Guid taskId,
        string mp3FilePath,
        string language,
        string quality,
        Action<int> onProgress)
    {
        // Встаем в очередь ожидания CPU ресурсов
        await _cpuLocker.WaitAsync();

        try
        {
            string outputFolder = Path.Combine(_environment.WebRootPath, "output");
            Directory.CreateDirectory(outputFolder);
            string whisperVavPath = Path.Combine(outputFolder, $"{taskId}_vocals.wav");
            string instrumentalWavPath = Path.Combine(outputFolder, $"{taskId}_instrumental.wav");

            // 1. Разделяем трек на вокал и инструментал (audio-separator)
            _audioProcessor.ConvertAndFilterMp3ToWav(mp3FilePath, whisperVavPath, instrumentalWavPath, quality, onProgress);
            onProgress.Invoke(25);

            // 2. СРАЗУ запускаем Silero VAD для поиска отрезков с речью (требует строго 16kHz!)
            var sileroModelPath = Path.Combine(_environment.ContentRootPath, "Models", "silero_vad.onnx");
            using var vadDetector = new SileroVadDetector(sileroModelPath);
            var vadIntervals = await vadDetector.GetSpeechIntervalsAsync(whisperVavPath);

            // Запись VAD отладочного дампа
            string debugDir = Path.Combine(_environment.WebRootPath, "output", $"debug_{taskId}");
            Directory.CreateDirectory(debugDir);
            File.WriteAllText(Path.Combine(debugDir, "1_vad_intervals.json"), System.Text.Json.JsonSerializer.Serialize(vadIntervals, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            // 3. Отправляем отфильтрованный ВОКАЛ (уже 16kHz) и VAD-интервалы в Whisper
            var task = await _context.KaraokeTasks.FindAsync(taskId);
            string? geminiApiKey = task?.GeminiApiKey;
            string? trackName = task != null ? Path.GetFileNameWithoutExtension(task.OriginalFileName) : null;

            var (words, detectedLanguage) = await _speechRecognizer.TranscribeAndMergeTokensAsync(
                whisperVavPath,
                language,
                p => onProgress.Invoke(25 + (p * 25 / 100)),
                geminiApiKey,
                trackName,
                vadIntervals); // <-- Передаем интервалы VAD в Whisper

            // Запись raw aligned words отладочного дампа
            File.WriteAllText(Path.Combine(debugDir, "2_aligned_words_raw.json"), System.Text.Json.JsonSerializer.Serialize(words, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            // Сохраняем определенный язык в БД, если было выбрано автоматическое определение
            if (task != null)
            {
                if (string.IsNullOrEmpty(task.Language) || task.Language == "auto")
                {
                    task.Language = detectedLanguage;
                    await _context.SaveChangesAsync();
                }
            }

            // 4. Группируем полученные слова в строчки караоке
            var generator = new AssSubtitleGenerator();
            var phrases = generator.GroupWordsIntoPhrases(words);

            // Запись итогового отладочного дампа фраз
            File.WriteAllText(Path.Combine(debugDir, "4_final_corrected_phrases.json"), System.Text.Json.JsonSerializer.Serialize(phrases, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            return phrases;
        }
        finally
        {
            // Освобождаем CPU локер для следующего трека в очереди
            _cpuLocker.Release();
        }
    }
}