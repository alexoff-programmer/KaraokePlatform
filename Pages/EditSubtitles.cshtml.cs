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

namespace KaraokePlatform.Pages;

[Authorize]
public class EditSubtitlesModel : PageModel
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _environment;
    private readonly ISubtitleGenerator _subtitleGenerator;
    private readonly VideoRenderer _videoRenderer;
    private readonly QueueChannel _queueChannel;

    public EditSubtitlesModel(AppDbContext context, IWebHostEnvironment environment, ISubtitleGenerator subtitleGenerator, VideoRenderer videoRenderer, QueueChannel queueChannel)
    {
        _context = context;
        _environment = environment;
        _subtitleGenerator = subtitleGenerator;
        _videoRenderer = videoRenderer;
        _queueChannel = queueChannel;
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
}