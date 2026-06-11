using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Interfaces;

namespace KaraokePlatform.Services.Audio;

public class WhisperTranscriber
{
    private readonly IAudioProcessor _audioProcessor;
    private readonly IAudioSilenceAnalyzer _silenceAnalyzer;
    private readonly ISpeechRecognizer _speechRecognizer;
    private readonly ISubtitleGenerator _subtitleGenerator;

    public WhisperTranscriber(
        IAudioProcessor audioProcessor,
        IAudioSilenceAnalyzer silenceAnalyzer,
        ISpeechRecognizer speechRecognizer,
        ISubtitleGenerator subtitleGenerator)
    {
        _audioProcessor = audioProcessor;
        _silenceAnalyzer = silenceAnalyzer;
        _speechRecognizer = speechRecognizer;
        _subtitleGenerator = subtitleGenerator;
    }

    public async Task<string> ProcessAudioAsync(string mp3FilePath, string outputFolder, string language, Action<int> onProgress)
    {
        // КРОССПЛАТФОРМЕННЫЙ ФИКС: Создаем temp строго внутри папки выполнения приложения
        string tempRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
        Directory.CreateDirectory(tempRoot);

        string wavPath = Path.Combine(tempRoot, $"{Guid.NewGuid()}.wav");
        string assFileName = $"{Guid.NewGuid()}.ass";
        string assOutputPath = Path.Combine(outputFolder, assFileName);
        string targetLanguage = string.IsNullOrWhiteSpace(language) ? "ru" : language;

        try
        {
            // 1. ИИ-разделение аудио (0% -> 25%)
            _audioProcessor.ConvertAndFilterMp3ToWav(mp3FilePath, wavPath, onProgress);

            onProgress.Invoke(25);

            // 2. Акустический анализ тишины по чистому вокалу
            var vocalIntervals = _silenceAnalyzer.GetVocalIntervals(wavPath, thresholdDb: -42.0);

            // 3. Распознавание речи Whisper (25% -> 50%)
            var words = await _speechRecognizer.TranscribeAndMergeTokensAsync(
                wavPath,
                targetLanguage,
                vocalIntervals,
                whisperProgress =>
                {
                    int scaledProgress = 25 + (whisperProgress * 25 / 100);
                    onProgress.Invoke(scaledProgress);
                });

            // 4. Формирование ASS файла субтитров
            onProgress.Invoke(55);
            string assContent = _subtitleGenerator.GenerateKaraokeMarkup(words);
            await File.WriteAllTextAsync(assOutputPath, assContent, Encoding.UTF8);

            onProgress.Invoke(60);
            return assOutputPath;
        }
        finally
        {
            if (File.Exists(wavPath))
            {
                try { File.Delete(wavPath); } catch { }
            }
        }
    }
}