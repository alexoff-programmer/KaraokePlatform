using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Interfaces;
using KaraokePlatform.Services.Audio.Records;
using Microsoft.AspNetCore.Hosting; // Добавлено

namespace KaraokePlatform.Services.Audio;

public class WhisperTranscriber
{
    private readonly IAudioProcessor _audioProcessor;
    private readonly IAudioSilenceAnalyzer _silenceAnalyzer;
    private readonly ISpeechRecognizer _speechRecognizer;
    private readonly IWebHostEnvironment _environment; // Инжектим окружение

    public WhisperTranscriber(
        IAudioProcessor audioProcessor,
        IAudioSilenceAnalyzer silenceAnalyzer,
        ISpeechRecognizer speechRecognizer,
        IWebHostEnvironment environment) // Добавлено в DI
    {
        _audioProcessor = audioProcessor;
        _silenceAnalyzer = silenceAnalyzer;
        _speechRecognizer = speechRecognizer;
        _environment = environment;
    }

    public async Task<List<List<WordTimeInfo>>> ProcessAudioToPhrasesAsync(
        Guid taskId,
        string mp3FilePath,
        string language,
        Action<int> onProgress)
    {
        // Используем строго WebRootPath для временного вокала (чтобы Docker не путал контекст)
        string tempRoot = Path.Combine(_environment.WebRootPath, "temp");
        Directory.CreateDirectory(tempRoot);
        string whisperVavPath = Path.Combine(tempRoot, $"{taskId}_whisper.wav");

        // ИСПРАВЛЕНО: Теперь путь к output железно берется из WebRootPath, как и на страницах Razor Pages
        string outputFolder = Path.Combine(_environment.WebRootPath, "output");
        Directory.CreateDirectory(outputFolder);
        string instrumentalWavPath = Path.Combine(outputFolder, $"{taskId}_instrumental.wav");

        try
        {
            // Передаем фиксированные пути в AudioProcessor
            _audioProcessor.ConvertAndFilterMp3ToWav(mp3FilePath, whisperVavPath, instrumentalWavPath, onProgress);
            onProgress.Invoke(25);

            var vocalIntervals = _silenceAnalyzer.GetVocalIntervals(whisperVavPath, thresholdDb: -42.0);

            var words = await _speechRecognizer.TranscribeAndMergeTokensAsync(
                whisperVavPath, language, vocalIntervals,
                p => onProgress.Invoke(25 + (p * 25 / 100)));

            var generator = new AssSubtitleGenerator();
            var phrases = generator.GroupWordsIntoPhrases(words);

            return phrases;
        }
        finally
        {
            if (File.Exists(whisperVavPath)) try { File.Delete(whisperVavPath); } catch { }
        }
    }
}