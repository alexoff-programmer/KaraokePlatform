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
        if (UploadedAudio == null || UploadedAudio.Length == 0)
        {
            TempData["ErrorMessage"] = "Пожалуйста, выберите корректный аудиофайл.";
            return RedirectToPage(); // ДЕЛАЕМ РЕДИРЕКТ (GET) ВМЕСТО RETURN PAGE()
        }

        if (UploadedAudio == null || UploadedAudio.Length == 0)
        {
            ErrorMessage = "Пожалуйста, выберите корректный аудиофайл.";
            await LoadUserTasksAsync();
            return Page();
        }

        // Проверяем расширение файла
        var extension = Path.GetExtension(UploadedAudio.FileName).ToLower();
        if (extension != ".mp3")
        {
            ErrorMessage = "Допускаются только файлы в формате .mp3";
            await LoadUserTasksAsync();
            return Page();
        }

        // Получаем ID текущего залогиненного юзера из куки
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out Guid userId))
        {
            return RedirectToPage("/Index");
        }

        // ЗАЩИТА: Проверяем, существует ли этот пользователь в актуальной БД
        bool userExists = await _context.Users.AnyAsync(u => u.Id == userId);
        if (!userExists)
        {
            // Если пользователя нет (базу сбросили), принудительно разлогиниваем его и отправляем на вход
            await HttpContext.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
            TempData["ErrorMessage"] = "Сессия устарела. Пожалуйста, войдите снова.";
            return RedirectToPage("/Index");
        }

        // Гарантируем, что папка для загрузок существует внутри wwwroot
        var uploadsFolder = Path.Combine(_environment.WebRootPath, "uploads");
        if (!Directory.Exists(uploadsFolder))
        {
            Directory.CreateDirectory(uploadsFolder);
        }

        // Генерируем уникальное имя файла на сервере
        var uniqueFileName = $"{Guid.NewGuid()}{extension}";
        var filePath = Path.Combine(uploadsFolder, uniqueFileName);

        // Физически сохраняем файл на диск
        using (var fileStream = new FileStream(filePath, FileMode.Create))
        {
            await UploadedAudio.CopyToAsync(fileStream);
        }

        // Сохраняем задачу в базу данных
        var newTask = new KaraokeTask
        {
            UserId = userId,
            OriginalFileName = UploadedAudio.FileName,
            AudioFilePath = Path.Combine("uploads", uniqueFileName), // относительный путь для веба
            Language = SelectedLanguage,
            Status = TaskStatus.InQueue,
            CreatedAt = DateTime.UtcNow
        };

        _context.KaraokeTasks.Add(newTask);
        await _context.SaveChangesAsync();

        SuccessMessage = "Файл успешно загружен и добавлен в очередь на обработку!";

        // Отправляем ID созданной задачи в фоновый воркер через канал
        await _queueChannel.AddTaskAsync(newTask.Id);

        // Кладем сообщение об успехе в TempData
        TempData["SuccessMessage"] = "Файл успешно загружен и добавлен в очередь на обработку!";

        return RedirectToPage();
    }

    private async Task LoadUserTasksAsync()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out Guid userId))
        {
            // Загружаем задачи текущего пользователя, сортируя по новизне
            UserTasks = await _context.KaraokeTasks
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();
        }
    }
}