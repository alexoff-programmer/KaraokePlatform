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

namespace KaraokePlatform.Pages;

[Authorize]
public class EditSubtitlesModel : PageModel
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _environment;
    private readonly ISubtitleGenerator _subtitleGenerator;
    private readonly VideoRenderer _videoRenderer;

    public EditSubtitlesModel(AppDbContext context, IWebHostEnvironment environment, ISubtitleGenerator subtitleGenerator, VideoRenderer videoRenderer)
    {
        _context = context;
        _environment = environment;
        _subtitleGenerator = subtitleGenerator;
        _videoRenderer = videoRenderer;
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

        // Фикс CS8602: Защита от null
        var originalPhrases = JsonSerializer.Deserialize<List<List<WordTimeInfo>>>(task.DetectedLinesJson ?? "[]") ?? new();

        if (originalPhrases.Count != EditedLines.Count)
        {
            ModelState.AddModelError("", "Ошибка: количество строк не совпадает.");
            return Page();
        }

        for (int i = 0; i < originalPhrases.Count; i++)
        {
            var oldPhraseWords = originalPhrases[i];
            var newTextLine = EditedLines[i]?.Trim() ?? string.Empty;

            var newWords = newTextLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Фикс CS8602: проверяем oldPhraseWords на null и на пустоту перед вызовом .First()/.Last()
            if (newWords.Length == 0 || oldPhraseWords == null || oldPhraseWords.Count == 0) continue;

            TimeSpan phraseStart = oldPhraseWords.First().Start;
            TimeSpan phraseEnd = oldPhraseWords.Last().End;
            double totalDurationMs = (phraseEnd - phraseStart).TotalMilliseconds;

            double totalChars = newWords.Sum(w => w.Length);
            if (totalChars == 0) totalChars = 1; // Защита от деления на ноль, если строка состоит из пробелов

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

        var outputFolder = Path.Combine(_environment.WebRootPath, "output");
        var assFileName = $"{task.Id}.ass"; // Привязано к task.Id
        var assOutputPath = Path.Combine(outputFolder, assFileName);

        string assContent = _subtitleGenerator.GenerateKaraokeMarkup(finalWordsList);
        await System.IO.File.WriteAllTextAsync(assOutputPath, assContent, Encoding.UTF8);

        var videoFileName = $"{Guid.NewGuid()}.mp4";
        var fullVideoOutputPath = Path.Combine(outputFolder, videoFileName);

        // ИСПРАВЛЕНО: Умный выбор аудиодорожки для финального рендеринга видео
        string fullAudioPath = Path.Combine(_environment.WebRootPath, task.AudioFilePath);
        string expectedInstrumentalPath = Path.Combine(outputFolder, $"{task.Id}_instrumental.wav");

        // Если в задаче стоит RemoveVocal и файл минусовки физически существует на диске — берем его!
        if (task.RemoveVocal && System.IO.File.Exists(expectedInstrumentalPath))
        {
            fullAudioPath = expectedInstrumentalPath;
        }

        string? fullBgPath = !string.IsNullOrEmpty(task.BackgroundImagePath) ? Path.Combine(_environment.WebRootPath, task.BackgroundImagePath) : null;

        task.Status = KaraokeTaskStatus.Processing;
        await _context.SaveChangesAsync();

        try
        {
            await _videoRenderer.RenderKaraokeVideoAsync(fullAudioPath, assOutputPath, fullVideoOutputPath, fullBgPath);

            task.Status = KaraokeTaskStatus.Completed;
            task.VideoFilePath = Path.Combine("output", videoFileName);

            // ИСПРАВЛЕНО: Раз уж видео успешно собралось с минусовкой, 
            // тяжелый wav-файл минусовки можно сразу удалить, чтобы не забивать сервер!
            if (task.RemoveVocal && System.IO.File.Exists(expectedInstrumentalPath))
            {
                try { System.IO.File.Delete(expectedInstrumentalPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            task.Status = KaraokeTaskStatus.Failed;
            task.ErrorMessage = $"Ошибка постобработки: {ex.Message}";
        }

        task.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return RedirectToPage("/Dashboard");
    }
}