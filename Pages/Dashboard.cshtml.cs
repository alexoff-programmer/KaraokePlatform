using System.Security.Claims;
using KaraokePlatform.Data;
using KaraokePlatform.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using KaraokeTaskStatus = KaraokePlatform.Data.Entities.KaraokeTaskStatus;

namespace KaraokePlatform.Pages;

[Authorize] // Страница доступна только вошедшим пользователям
public class DashboardModel : PageModel
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _environment;

    public DashboardModel(AppDbContext context, IWebHostEnvironment environment)
    {
        _context = context;
        _environment = environment;
    }

    [BindProperty]
    public IFormFile? UploadedAudio { get; set; }

    public List<KaraokeTask> UserTasks { get; set; } = new();

    public string ErrorMessage { get; set; } = string.Empty;
    public string SuccessMessage { get; set; } = string.Empty;

    // Метод срабатывает при открытии страницы
    public async Task<IActionResult> OnGetAsync()
    {
        await LoadUserTasksAsync();
        return Page();
    }

    // Метод срабатывает при загрузке MP3 файла
    public async Task<IActionResult> OnPostUploadAsync()
    {
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
            Status = KaraokeTaskStatus.InQueue,
            CreatedAt = DateTime.UtcNow
        };

        _context.KaraokeTasks.Add(newTask);
        await _context.SaveChangesAsync();

        SuccessMessage = "Файл успешно загружен и добавлен в очередь на обработку!";

        // TODO: Здесь мы чуть позже будем отправлять ID задачи в фоновую очередь обработки!

        await LoadUserTasksAsync();
        return Page();
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