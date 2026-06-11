using System.Security.Claims;
using KaraokePlatform.Data;
using KaraokePlatform.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TaskStatus = KaraokePlatform.Data.Entities.KaraokeTaskStatus;
using KaraokePlatform.Services.Background;
using Microsoft.AspNetCore.Authentication;

namespace KaraokePlatform.Pages;

[Authorize] // Страница доступна только вошедшим пользователям
public class DashboardModel : PageModel
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _environment;
    private readonly QueueChannel _queueChannel;

    public DashboardModel(AppDbContext context, IWebHostEnvironment environment, QueueChannel queueChannel)
    {
        _context = context;
        _environment = environment;
        _queueChannel = queueChannel;
    }

    [BindProperty]
    public IFormFile? UploadedAudio { get; set; }

    [BindProperty]
    public string SelectedLanguage { get; set; } = "auto";

    [BindProperty]
    public IFormFile? UploadedBackground { get; set; }

    public List<KaraokeTask> UserTasks { get; set; } = new();

    public string ErrorMessage { get; set; } = string.Empty;
    public string SuccessMessage { get; set; } = string.Empty;

    // Метод для выхода из системы (кнопка Выйти)
    public async Task<IActionResult> OnPostLogoutAsync()
    {
        // Удаляем шифрованную куку авторизации из браузера
        await HttpContext.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);

        // Переносим сообщение через TempData на главную
        TempData["SuccessMessage"] = "Вы успешно вышли из системы.";

        // Возвращаем пользователя на страницу входа
        return RedirectToPage("/Index");
    }

    // Метод срабатывает при открытии страницы
    public async Task<IActionResult> OnGetAsync()
    {
        // Восстанавливаем сообщения из TempData после редиректа, если они там есть
        ErrorMessage = TempData["ErrorMessage"] as string ?? string.Empty;
        SuccessMessage = TempData["SuccessMessage"] as string ?? string.Empty;

        await LoadUserTasksAsync();
        return Page();
    }

    // Метод срабатывает при загрузке MP3 файла
    public async Task<IActionResult> OnPostUploadAsync()
    {
        // Валидация аудио
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

        // Валидация изображения
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

            // Создаем отдельную папку для фонов внутри wwwroot
            var bgFolder = Path.Combine(_environment.WebRootPath, "uploads", "backgrounds");
            if (!Directory.Exists(bgFolder))
            {
                Directory.CreateDirectory(bgFolder);
            }

            var uniqueBgName = $"{Guid.NewGuid()}{bgExtension}";
            var bgFilePath = Path.Combine(bgFolder, uniqueBgName);

            // Сохраняем картинку на диск
            using (var fileStream = new FileStream(bgFilePath, FileMode.Create))
            {
                await UploadedBackground.CopyToAsync(fileStream);
            }

            // Формируем относительный путь для сохранения в БД
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

        // Сохранение аудиофайла на диск
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

        // Запись в базу данных
        var newTask = new KaraokeTask
        {
            UserId = userId,
            OriginalFileName = UploadedAudio.FileName,
            AudioFilePath = Path.Combine("uploads", uniqueAudioName),
            BackgroundImagePath = dbBackgroundPath, // <-- ПЕРЕДАЕМ ПУТЬ К КАРТИНКЕ (или null, если файла нет)
            Language = SelectedLanguage,
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
            // Находим задачи, которые "зависли" в обработке (например, более 15 минут)
            var timeoutLimit = DateTime.UtcNow.AddMinutes(-15);

            var staleTasks = await _context.KaraokeTasks
                .Where(t => t.UserId == userId &&
                            (t.Status == TaskStatus.Processing || t.Status == TaskStatus.InQueue) &&
                            t.CreatedAt < timeoutLimit)
                .ToListAsync();

            // Если нашли такие задачи, переводим их в статус Failed
            if (staleTasks.Any())
            {
                foreach (var task in staleTasks)
                {
                    task.Status = TaskStatus.Failed;
                    task.ErrorMessage = "Превышено время ожидания. Похоже, процесс был прерван.";
                }

                // Сохраняем изменения автоматически при заходе пользователя на страницу
                await _context.SaveChangesAsync();
            }

            // Загружаем задачи текущего пользователя, сортируя по новизне
            UserTasks = await _context.KaraokeTasks
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();
        }
    }
}