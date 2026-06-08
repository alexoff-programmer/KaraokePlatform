using System.Text;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace KaraokePlatform.Services.Audio;

public class WhisperTranscriber
{
    private readonly string _modelPath;

    public WhisperTranscriber(IWebHostEnvironment environment)
    {
        // Путь к скачанной модели в корне проекта
        _modelPath = Path.Combine(environment.ContentRootPath, "Models", "ggml-base.bin");
    }

    public async Task<string> TranscribeToAssAsync(string inputMp3Path, string outputFolder, string languageCode = "auto")
    {
        if (!File.Exists(_modelPath))
        {
            throw new FileNotFoundException($"Файл модели Whisper не найден по пути: {_modelPath}. Пожалуйста, скачайте ggml-base.bin");
        }

        // 1. Конвертируем MP3 в Wav 16kHz Mono 16-bit PCM (требование Whisper)
        string tempWav = Path.Combine(outputFolder, $"{Guid.NewGuid()}.wav");
        ConvertToWhisperWav(inputMp3Path, tempWav);

        var assPath = Path.Combine(outputFolder, $"{Guid.NewGuid()}.ass");

        try
        {
            // 2. Инициализируем фабрику Whisper
            using var factory = WhisperFactory.FromPath(_modelPath);

            var processorBuilder = factory.CreateBuilder();

            // Если выбран конкретный язык, жестко задаем его. 
            // Если "auto" — Whisper сам определит язык по первым секундам аудио!
            if (!string.Equals(languageCode, "auto", StringComparison.OrdinalIgnoreCase))
            {
                processorBuilder.WithLanguage(languageCode);
            }
            else
            {
                processorBuilder.WithLanguageDetection(); // Включаем автоопределение языка
            }

            using var processor = processorBuilder
                .WithPrintProgress()
                .Build();

            using var fileStream = File.OpenRead(tempWav);

            // Начинаем разметку .ASS файла (Advanced SubStation Alpha)
            var assBuilder = new StringBuilder();
            BuildAssHeader(assBuilder);

            // Прогоняем аудио через нейросеть
            await foreach (var result in processor.ProcessAsync(fileStream))
            {
                // Для полноценного караоке-эффекта (\k) нам нужны послоговые тайминги, 
                // но базовый API Whisper.net выдает сегменты предложений.
                // Формируем стандартную строку диалога с мягким караоке-эффектом на сегмент:
                var start = FormatAssTime(result.Start);
                var end = FormatAssTime(result.End);
                var text = result.Text.Trim();

                if (!string.IsNullOrEmpty(text))
                {
                    // Накладываем базовый караоке-эффект закрашивания на весь сегмент строки
                    // \k[длительность в центисекундах]
                    var durationCentiSec = (int)(result.End - result.Start).TotalMilliseconds / 10;
                    assBuilder.AppendLine($"Dialogue: 0,{start},{end},KaraokeStyle,,0,0,0,,{{\\k{durationCentiSec}}}{text}");
                }
            }

            await File.WriteAllTextAsync(assPath, assBuilder.ToString(), Encoding.UTF8);
        }
        finally
        {
            // Удаляем временный WAV файл, чтобы не забивать диск
            if (File.Exists(tempWav)) File.Delete(tempWav);
        }

        return assPath; // Возвращаем путь к готовому файлу субтитров
    }

    private void ConvertToWhisperWav(string sourceMp3, string targetWav)
    {
        using var reader = new AudioFileReader(sourceMp3);

        // Ресемплинг в 16000Hz, 1 канал (Mono)
        var outFormat = new WaveFormat(16000, 16, 1);
        using var resampler = new MediaFoundationResampler(reader, outFormat);

        WaveFileWriter.CreateWaveFile(targetWav, resampler);
    }

    private string FormatAssTime(TimeSpan ts)
    {
        // Формат времени для ASS: Ч:ММ:СС.ЦЦ (ЦЦ - сотые доли секунды)
        return $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
    }

    private void BuildAssHeader(StringBuilder sb)
    {
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine("PlayResX: 1080");
        sb.AppendLine("PlayResY: 1920"); // Вертикальное разрешение под Shorts/Reels
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        // Создаем караоке стиль: PrimaryColour - цвет закрашенного (белый), SecondaryColour - цвет ожидания (желтый/синий)
        sb.AppendLine("Style: KaraokeStyle,Arial,48,&H00FFFFFF,&H0000FFFF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,3,0,2,10,10,200,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
    }
}