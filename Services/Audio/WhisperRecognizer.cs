using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KaraokePlatform.Services.Audio.Interfaces;
using KaraokePlatform.Services.Audio.Records;
using KaraokePlatform.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.Wave;

namespace KaraokePlatform.Services.Audio;

public class WhisperRecognizer : ISpeechRecognizer
{
    private readonly string _modelPath;
    private readonly MmsForceAligner _forceAligner;
    private WhisperFactory? _factory;
    private readonly object _lock = new object();

    public WhisperRecognizer(
        IOptions<WhisperSettings> settings, 
        IWebHostEnvironment environment,
        MmsForceAligner forceAligner)
    {
        var path = settings.Value.ModelPath;
        _modelPath = Path.IsPathRooted(path) ? path : Path.Combine(environment.ContentRootPath, path);
        _forceAligner = forceAligner;
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
        Action<int> onProgress)
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

        onProgress?.Invoke(75);

        // Производим пофразовое выравнивание с помощью ONNX Force Aligner
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];

            // Нарезаем сэмплы вокала для данного сегмента
            int startSample = (int)(segment.Start.TotalSeconds * 16000);
            int endSample = (int)(segment.End.TotalSeconds * 16000);

            if (startSample < 0) startSample = 0;
            if (endSample > allSamples.Length) endSample = allSamples.Length;
            if (startSample >= endSample) continue;

            int sampleCount = endSample - startSample;
            var segmentSamples = new float[sampleCount];
            Array.Copy(allSamples, startSample, segmentSamples, 0, sampleCount);

            // Выравниваем слова внутри сегмента
            var segmentWords = await _forceAligner.AlignAudioAsync(segmentSamples, segment.Text, language);

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
                    Start = segment.Start.Add(w.Start),
                    End = segment.Start.Add(w.End)
                });
            }

            // Обновляем прогресс выравнивания
            int currentProgress = 75 + (i * 20 / segments.Count);
            onProgress?.Invoke(currentProgress);
        }

        onProgress?.Invoke(100);
        return alignedWords;
    }
}