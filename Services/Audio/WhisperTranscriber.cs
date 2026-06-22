using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Interfaces;
using KaraokePlatform.Services.Audio.Records;

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

    // ИСПРАВЛЕНО: Теперь принимает bool removeVocal и возвращает объект TranscriberResult
    // Измени возвращаемый тип на список фраз (List<List<WordTimeInfo>>)
    public async Task<List<List<WordTimeInfo>>> ProcessAudioToPhrasesAsync(
    Guid taskId,
    string mp3FilePath,
    string language,
    Action<int> onProgress)
    {
        // Кросплатформенный temp
        string tempRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
        Directory.CreateDirectory(tempRoot);

        // Использовали taskId для входящего вокала
        string whisperVavPath = Path.Combine(tempRoot, $"{taskId}_whisper.wav");

        // ЖЕСТКИЙ ФИКС: Для минусовки в папке output ТАКЖЕ используем taskId вместо Guid!
        // Теперь имя файла будет вида: "wwwroot/output/ИД_ЗАДАЧИ_instrumental.wav"
        string outputFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "output");
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
            // Делаем метод GroupWordsIntoPhrases публичным в AssSubtitleGenerator
            var phrases = generator.GroupWordsIntoPhrases(words);

            return phrases;
        }
        finally
        {
            if (File.Exists(whisperVavPath)) try { File.Delete(whisperVavPath); } catch { }
        }
    }
}