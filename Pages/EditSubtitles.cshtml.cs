using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KaraokePlatform.Data;
using KaraokePlatform.Data.Entities;
using KaraokePlatform.Services.Audio.Records;
using KaraokePlatform.Services.Audio.Interfaces;
using KaraokePlatform.Services.Video;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using KaraokePlatform.Services.Background;
using KaraokePlatform.Hubs;
using Microsoft.AspNetCore.SignalR;

using System.Net.Http;
using KaraokePlatform.Services.Audio;

namespace KaraokePlatform.Pages;

[Authorize]
public class EditSubtitlesModel : PageModel
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _environment;
    private readonly ISubtitleGenerator _subtitleGenerator;
    private readonly VideoRenderer _videoRenderer;
    private readonly QueueChannel _queueChannel;
    private readonly MmsForceAligner _forceAligner;
    private readonly IAudioProcessor _audioProcessor;

    public EditSubtitlesModel(
        AppDbContext context, 
        IWebHostEnvironment environment, 
        ISubtitleGenerator subtitleGenerator, 
        VideoRenderer videoRenderer, 
        QueueChannel queueChannel,
        MmsForceAligner forceAligner,
        IAudioProcessor audioProcessor)
    {
        _context = context;
        _environment = environment;
        _subtitleGenerator = subtitleGenerator;
        _videoRenderer = videoRenderer;
        _queueChannel = queueChannel;
        _forceAligner = forceAligner;
        _audioProcessor = audioProcessor;
    }

    [BindProperty]
    public Guid TaskId { get; set; }

    [BindProperty]
    public List<string> EditedLines { get; set; } = new();

    public string OriginalFileName { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid taskId)
    {
        TaskId = taskId;
        var task = await _context.KaraokeTasks.FirstOrDefaultAsync(t => t.Id == taskId);

        if (task == null || task.Status != KaraokeTaskStatus.AwaitingReview)
        {
            return RedirectToPage("/Dashboard");
        }

        OriginalFileName = task.OriginalFileName;

        // Фикс CS8602: Защита от null с помощью оператора ??
        var phrases = JsonSerializer.Deserialize<List<List<WordTimeInfo>>>(task.DetectedLinesJson ?? "[]") ?? new();

        foreach (var phrase in phrases)
        {
            // Фикс CS8602: Добавлена проверка, чтобы w.Text не упал, если Whisper выдал пустую структуру
            var lineText = string.Join(" ", phrase.Where(w => w != null && w.Text != null).Select(w => w.Text));
            EditedLines.Add(lineText);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var task = await _context.KaraokeTasks.FirstOrDefaultAsync(t => t.Id == TaskId);
        if (task == null) return RedirectToPage("/Dashboard");

        var originalPhrases = JsonSerializer.Deserialize<List<List<WordTimeInfo>>>(task.DetectedLinesJson ?? "[]") ?? new();

        if (originalPhrases.Count != EditedLines.Count)
        {
            ModelState.AddModelError("", "Ошибка: количество строк не совпадает.");
            return Page();
        }

        // 1. Пересчитываем тайминги (этот блок без изменений)
        for (int i = 0; i < originalPhrases.Count; i++)
        {
            var oldPhraseWords = originalPhrases[i];
            var newTextLine = EditedLines[i]?.Trim() ?? string.Empty;
            var newWords = newTextLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (newWords.Length == 0 || oldPhraseWords == null || oldPhraseWords.Count == 0) continue;

            TimeSpan phraseStart = oldPhraseWords.First().Start;
            TimeSpan phraseEnd = oldPhraseWords.Last().End;
            double totalDurationMs = (phraseEnd - phraseStart).TotalMilliseconds;

            double totalChars = newWords.Sum(w => w.Length);
            if (totalChars == 0) totalChars = 1;

            var redistributedWords = new List<WordTimeInfo>();
            TimeSpan currentTrackTime = phraseStart;

            for (int j = 0; j < newWords.Length; j++)
            {
                double weight = newWords[j].Length / totalChars;
                double wordDurationMs = totalDurationMs * weight;

                redistributedWords.Add(new WordTimeInfo
                {
                    Text = newWords[j],
                    Start = currentTrackTime,
                    End = currentTrackTime + TimeSpan.FromMilliseconds(wordDurationMs)
                });

                currentTrackTime = redistributedWords.Last().End;
            }

            originalPhrases[i] = redistributedWords;
        }

        var finalWordsList = originalPhrases.Where(p => p != null).SelectMany(p => p).ToList();

        // 2. Генерируем чистовые субтитры .ass
        var outputFolder = Path.Combine(_environment.WebRootPath, "output");
        var assFileName = $"{task.Id}.ass";
        var assOutputPath = Path.Combine(outputFolder, assFileName);

        string assContent = _subtitleGenerator.GenerateKaraokeMarkup(finalWordsList);
        await System.IO.File.WriteAllTextAsync(assOutputPath, assContent, Encoding.UTF8);

        // 3. Обновляем JSON фраз в БД, чтобы зафиксировать правки текста
        task.DetectedLinesJson = JsonSerializer.Serialize(originalPhrases);

        // ЖЕСТКИЙ ФИКС: Вместо ручного рендеринга переводим задачу в статус рендеринга 
        // и отправляем обратно в фоновую очередь!
        task.Status = KaraokeTaskStatus.ReadyToRender;
        task.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Добавляем ID задачи в QueueChannel, чтобы воркер проснулся и начал рендерить видео
        // (Для этого инжектируй QueueChannel _queueChannel через конструктор EditSubtitlesModel)
        await _queueChannel.AddTaskAsync(task.Id);

        var user = await _context.Users.FindAsync(task.UserId);
        string username = user?.Username ?? string.Empty;

        if (!string.IsNullOrEmpty(username))
        {
            var hubContext = HttpContext.RequestServices.GetRequiredService<IHubContext<NotificationHub>>();
            await hubContext.Clients.Group(username)
                .SendAsync("UpdateTaskStatus", task.Id.ToString(), "ReadyToRender", null);
        }

        return RedirectToPage("/Dashboard");
    }

    public class GeminiRequest
    {
        public string GeminiApiKey { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnPostImproveWithGeminiAsync([FromBody] GeminiRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.GeminiApiKey))
        {
            return BadRequest("Необходим корректный Gemini API Key.");
        }

        var task = await _context.KaraokeTasks.FirstOrDefaultAsync(t => t.Id == TaskId);
        if (task == null) return NotFound("Задача не найдена.");

        var originalPhrases = JsonSerializer.Deserialize<List<List<WordTimeInfo>>>(task.DetectedLinesJson ?? "[]") ?? new();
        if (originalPhrases.Count == 0) return BadRequest("Текст песни пуст или еще не распознан.");

        // 1. Collect current segment texts
        var currentLines = originalPhrases.Select(phrase => 
            string.Join(" ", phrase.Where(w => w != null && w.Text != null).Select(w => w.Text))
        ).ToList();

        // 2. Call Gemini for lyrics correction
        var correctedLines = await ImproveLinesWithGeminiAsync(currentLines, request.GeminiApiKey);

        if (correctedLines.Count != originalPhrases.Count)
        {
            return new JsonResult(new { success = false, message = "Gemini вернул некорректное количество строк." });
        }

        // 3. Re-run force alignment on the audio for each line using the corrected text
        string cleanAudioRelative = task.AudioFilePath.TrimStart('\\', '/');
        string fullAudioPath = Path.Combine(_environment.WebRootPath, cleanAudioRelative);

        if (!System.IO.File.Exists(fullAudioPath))
        {
            return new JsonResult(new { success = false, message = "Файл вокала на сервере не найден." });
        }

        float[] allSamples;
        using (var fileStream = System.IO.File.OpenRead(fullAudioPath))
        {
            var waveParser = new Whisper.net.Wave.WaveParser(fileStream);
            allSamples = await waveParser.GetAvgSamplesAsync();
        }

        var updatedPhrases = new List<List<WordTimeInfo>>();

        for (int i = 0; i < originalPhrases.Count; i++)
        {
            var oldPhraseWords = originalPhrases[i];
            var newTextLine = correctedLines[i]?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(newTextLine) || oldPhraseWords.Count == 0)
            {
                updatedPhrases.Add(oldPhraseWords);
                continue;
            }

            // Get original boundary timestamps
            var phraseStart = oldPhraseWords.First().Start;
            var phraseEnd = oldPhraseWords.Last().End;

            // Apply 150ms leading padding and 350ms trailing padding (Audio Padding)
            var padLeft = TimeSpan.FromMilliseconds(150);
            var padRight = TimeSpan.FromMilliseconds(350);

            var paddedStart = phraseStart - padLeft;
            if (paddedStart < TimeSpan.Zero) paddedStart = TimeSpan.Zero;

            var paddedEnd = phraseEnd + padRight;
            double totalDurationSec = allSamples.Length / 16000.0;
            if (paddedEnd > TimeSpan.FromSeconds(totalDurationSec))
                paddedEnd = TimeSpan.FromSeconds(totalDurationSec);

            var sliceStart = paddedStart;
            var sliceEnd = paddedEnd;
            var segmentSamples = _audioProcessor.SliceSamples(allSamples, ref sliceStart, sliceEnd);

            if (segmentSamples.Length == 0)
            {
                updatedPhrases.Add(oldPhraseWords);
                continue;
            }

            try
            {
                // Align corrected text to audio slice
                var segmentWords = await _forceAligner.AlignAudioAsync(segmentSamples, newTextLine, task.Language, 5);

                var alignedWords = new List<WordTimeInfo>();
                foreach (var w in segmentWords)
                {
                    var cleanWordText = w.Text.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '`', '(', ')', '[', ']', '{', '}', '_', '*', '…', '-');
                    if (string.IsNullOrWhiteSpace(cleanWordText)) continue;

                    cleanWordText = cleanWordText.Replace("ё", "е").Replace("Ё", "Е");

                    alignedWords.Add(new WordTimeInfo
                    {
                        Text = cleanWordText,
                        Start = sliceStart.Add(w.Start),
                        End = sliceStart.Add(w.End)
                    });
                }

                if (alignedWords.Count > 0)
                {
                    updatedPhrases.Add(alignedWords);
                }
                else
                {
                    // Fallback to proportional interpolation if alignment is empty
                    updatedPhrases.Add(RedistributeProportional(oldPhraseWords, newTextLine));
                }
            }
            catch (Exception)
            {
                // Fallback to proportional interpolation on error
                updatedPhrases.Add(RedistributeProportional(oldPhraseWords, newTextLine));
            }
        }

        // 4. Save to task DB
        task.DetectedLinesJson = JsonSerializer.Serialize(updatedPhrases);
        await _context.SaveChangesAsync();

        return new JsonResult(new { success = true });
    }

    private List<WordTimeInfo> RedistributeProportional(List<WordTimeInfo> oldPhraseWords, string newTextLine)
    {
        var newWords = newTextLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (newWords.Length == 0 || oldPhraseWords == null || oldPhraseWords.Count == 0) return oldPhraseWords ?? new();

        TimeSpan phraseStart = oldPhraseWords.First().Start;
        TimeSpan phraseEnd = oldPhraseWords.Last().End;
        double totalDurationMs = (phraseEnd - phraseStart).TotalMilliseconds;

        double totalChars = newWords.Sum(w => w.Length);
        if (totalChars == 0) totalChars = 1;

        var redistributedWords = new List<WordTimeInfo>();
        TimeSpan currentTrackTime = phraseStart;

        for (int j = 0; j < newWords.Length; j++)
        {
            double weight = newWords[j].Length / totalChars;
            double wordDurationMs = totalDurationMs * weight;

            redistributedWords.Add(new WordTimeInfo
            {
                Text = newWords[j],
                Start = currentTrackTime,
                End = currentTrackTime + TimeSpan.FromMilliseconds(wordDurationMs)
            });

            currentTrackTime = redistributedWords.Last().End;
        }

        return redistributedWords;
    }

    private async Task<List<string>> ImproveLinesWithGeminiAsync(List<string> currentLines, string apiKey)
    {
        try
        {
            var jsonInput = JsonSerializer.Serialize(currentLines);

            var promptText = "You are an expert lyrics editor. " +
                             "Translate and correct the following raw voice recognition segments of a song into correct, grammatically clean lyrics. " +
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

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={apiKey}";
            
            var response = await httpClient.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Gemini API error status code {response.StatusCode}");
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
                        return correctedTexts;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Gemini Manual Improvement Error] {ex.Message}");
        }

        return currentLines;
    }
}