using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using KaraokePlatform.Data;
using KaraokePlatform.Data.Entities;
using KaraokePlatform.Services.Background;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskStatus = KaraokePlatform.Data.Entities.KaraokeTaskStatus;

namespace KaraokePlatform.Pages;

[Authorize] // Страница доступна только вошедшим пользователям
public class DashboardModel : PageModel
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _environment;
    private readonly QueueChannel _queueChannel;
    private readonly TaskCancellationManager _cancellationManager;
    private readonly ILogger<DashboardModel> _logger; // ИСПРАВЛЕНО: Добавлено отсутствующее поле логгера

    public DashboardModel(
        AppDbContext context,
        IWebHostEnvironment environment,
        QueueChannel queueChannel,
        TaskCancellationManager cancellationManager,
        ILogger<DashboardModel> logger) // ИСПРАВЛЕНО: Инжектим логгер в конструктор
    {
        _context = context;
        _environment = environment;
        _queueChannel = queueChannel;
        _cancellationManager = cancellationManager;
        _logger = logger;
    }

    [BindProperty]
    public IFormFile? UploadedAudio { get; set; }

    [BindProperty]
    public string SelectedLanguage { get; set; } = "auto";

    [BindProperty]
    public IFormFile? UploadedBackground { get; set; }

    [BindProperty]
    public bool RemoveVocal { get; set; } = true; // по умолчанию включено

    public List<KaraokeTask> UserTasks { get; set; } = new();

    public string ErrorMessage { get; set; } = string.Empty;
    public string SuccessMessage { get; set; } = string.Empty;

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
        TempData["SuccessMessage"] = "Вы успешно вышли из системы.";
        return RedirectToPage("/Index");
    }

    public async Task<IActionResult> OnGetAsync()
    {
        ErrorMessage = TempData["ErrorMessage"] as string ?? string.Empty;
        SuccessMessage = TempData["SuccessMessage"] as string ?? string.Empty;

        await LoadUserTasksAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync()
    {
        if (UploadedAudio == null || UploadedAudio.Length == 0)
        {
            TempData["ErrorMessage"] = "Пожалуйста, выберите корректный аудиофайл.";
            return RedirectToPage();
        }

        var audioExtension = Path.GetExtension(UploadedAudio.FileName).ToLower();
        if (audioExtension != ".mp3")
        {
            TempData["ErrorMessage"] = "Допускаются только файлы в формате .mp3";
            return RedirectToPage();
        }

        string? dbBackgroundPath = null;
        if (UploadedBackground != null && UploadedBackground.Length > 0)
        {
            var bgExtension = Path.GetExtension(UploadedBackground.FileName).ToLower();
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png" };

            if (!allowedExtensions.Contains(bgExtension))
            {
                TempData["ErrorMessage"] = "Для фона допускаются только изображения (.jpg, .jpeg, .png)";
                return RedirectToPage();
            }

            var bgFolder = Path.Combine(_environment.WebRootPath, "uploads", "backgrounds");
            if (!Directory.Exists(bgFolder))
            {
                Directory.CreateDirectory(bgFolder);
            }

            var uniqueBgName = $"{Guid.NewGuid()}{bgExtension}";
            var bgFilePath = Path.Combine(bgFolder, uniqueBgName);

            using (var fileStream = new FileStream(bgFilePath, FileMode.Create))
            {
                await UploadedBackground.CopyToAsync(fileStream);
            }

            dbBackgroundPath = Path.Combine("uploads", "backgrounds", uniqueBgName);
        }

        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out Guid userId))
        {
            return RedirectToPage("/Index");
        }

        bool userExists = await _context.Users.AnyAsync(u => u.Id == userId);
        if (!userExists)
        {
            await HttpContext.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
            TempData["ErrorMessage"] = "Сессия устарела. Пожалуйста, войдите снова.";
            return RedirectToPage("/Index");
        }

        var uploadsFolder = Path.Combine(_environment.WebRootPath, "uploads");
        if (!Directory.Exists(uploadsFolder))
        {
            Directory.CreateDirectory(uploadsFolder);
        }

        var uniqueAudioName = $"{Guid.NewGuid()}{audioExtension}";
        var audioFilePath = Path.Combine(uploadsFolder, uniqueAudioName);

        using (var fileStream = new FileStream(audioFilePath, FileMode.Create))
        {
            await UploadedAudio.CopyToAsync(fileStream);
        }

        var newTask = new KaraokeTask
        {
            UserId = userId,
            OriginalFileName = UploadedAudio.FileName,
            AudioFilePath = Path.Combine("uploads", uniqueAudioName),
            BackgroundImagePath = dbBackgroundPath,
            Language = SelectedLanguage,
            RemoveVocal = this.RemoveVocal,
            Status = TaskStatus.InQueue,
            CreatedAt = DateTime.UtcNow
        };

        _context.KaraokeTasks.Add(newTask);
        await _context.SaveChangesAsync();

        await _queueChannel.AddTaskAsync(newTask.Id);

        TempData["SuccessMessage"] = "Файл успешно загружен и добавлен в очередь на обработку!";
        return RedirectToPage();
    }

    private async Task LoadUserTasksAsync()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out Guid userId))
        {
            var timeoutLimit = DateTime.UtcNow.AddMinutes(-15);

            var staleTasks = await _context.KaraokeTasks
                .Where(t => t.UserId == userId &&
                            (t.Status == TaskStatus.Processing || t.Status == TaskStatus.InQueue) &&
                            t.CreatedAt < timeoutLimit)
                .ToListAsync();

            if (staleTasks.Any())
            {
                foreach (var task in staleTasks)
                {
                    task.Status = TaskStatus.Failed;
                    task.ErrorMessage = "Превышено время ожидания. Похоже, процесс был прерван.";
                }
                await _context.SaveChangesAsync();
            }

            UserTasks = await _context.KaraokeTasks
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid taskId)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out Guid userId))
        {
            return RedirectToPage("/Index");
        }

        var task = await _context.KaraokeTasks
            .FirstOrDefaultAsync(t => t.Id == taskId && t.UserId == userId);

        if (task == null)
        {
            TempData["ErrorMessage"] = "Задача не найдена.";
            return RedirectToPage();
        }

        // ИСПРАВЛЕНИЕ: Отменяем, если задача выполняется ИЛИ находится в очереди сборки
        if (task.Status == TaskStatus.Processing || task.Status == TaskStatus.ReadyToRender)
        {
            _cancellationManager.CancelTask(task.Id);

            // Умное ожидание освобождения задачи воркером
            int attempts = 0;
            while (attempts < 20)
            {
                await Task.Delay(100);

                // Проверяем через контекст (или NoTracking), изменил ли воркер статус на Failed/отменил ли
                var currentStatus = await _context.KaraokeTasks
                    .AsNoTracking()
                    .Where(t => t.Id == taskId)
                    .Select(t => t.Status)
                    .FirstOrDefaultAsync();

                // Если воркер уже перехватил отмену и завершил работу с задачей — выходим досрочно!
                if (currentStatus == TaskStatus.Failed || currentStatus == TaskStatus.Completed)
                {
                    break;
                }
                attempts++;
            }
        }

        try
        {
            // 1. Удаляем исходный аудиофайл
            if (!string.IsNullOrEmpty(task.AudioFilePath))
            {
                var cleanAudioPath = task.AudioFilePath.TrimStart('\\', '/');
                var fullAudioPath = Path.Combine(_environment.WebRootPath, cleanAudioPath);
                DeleteFileWithRetry(fullAudioPath);
            }

            // 2. Удаляем готовый видеофайл (.mp4)
            if (!string.IsNullOrEmpty(task.VideoFilePath))
            {
                var cleanVideoPath = task.VideoFilePath.TrimStart('\\', '/');
                var fullVideoPath = Path.Combine(_environment.WebRootPath, cleanVideoPath);
                DeleteFileWithRetry(fullVideoPath);
            }

            // 3. Удаляем фоновое изображение
            if (!string.IsNullOrEmpty(task.BackgroundImagePath))
            {
                var cleanBgPath = task.BackgroundImagePath.TrimStart('\\', '/');
                var fullBgPath = Path.Combine(_environment.WebRootPath, cleanBgPath);
                DeleteFileWithRetry(fullBgPath);
            }

            // 4. Очистка временных файлов (.ass, .wav минусовки)
            var outputFolder = Path.Combine(_environment.WebRootPath, "output");
            if (Directory.Exists(outputFolder))
            {
                var directoryInfo = new DirectoryInfo(outputFolder);
                var taskFiles = directoryInfo.GetFiles($"*{task.Id}*");
                foreach (var file in taskFiles)
                {
                    DeleteFileWithRetry(file.FullName);
                }
            }

            // Удаляем запись из базы данных только когда подчистили хвосты
            _context.KaraokeTasks.Remove(task);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Генерация и все связанные файлы успешно удалены.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Ошибка при полном удалении задачи {taskId}");
            TempData["ErrorMessage"] = $"Не удалось полностью удалить задачу. Ошибка доступа к файлам: {ex.Message}";
        }

        return RedirectToPage();
    }

    // Хелпер для удаления "капризных" файлов, которые ОС может закрывать с задержкой в пару миллисекунд
    private void DeleteFileWithRetry(string filePath)
    {
        if (!System.IO.File.Exists(filePath)) return;

        for (int i = 0; i < 3; i++)
        {
            try
            {
                System.IO.File.Delete(filePath);
                return; // Успешно удалено
            }
            catch (IOException)
            {
                // Если файл занят — чуть-чуть ждем и пробуем снова
                System.Threading.Thread.Sleep(200);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Не удалось удалить файл {filePath}: {ex.Message}");
                break;
            }
        }
    }
}