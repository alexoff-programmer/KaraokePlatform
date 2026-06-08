using KaraokePlatform.Data;
using KaraokePlatform.Data.Entities;
using KaraokePlatform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KaraokePlatform.Pages.Admin;

[Authorize(Roles = "Admin")] // Доступ только для администраторов
public class ManageUsersModel : PageModel
{
    private readonly AppDbContext _context;
    private readonly UserService _userService;

    public ManageUsersModel(AppDbContext context, UserService userService)
    {
        _context = context;
        _userService = userService;
    }

    // Модель для формы создания нового пользователя
    [BindProperty]
    public string NewUsername { get; set; } = string.Empty;

    [BindProperty]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    public Role NewRole { get; set; } = Role.User;

    // Список всех пользователей для отображения в таблице
    public List<AppUser> ExistingUsers { get; set; } = new();

    public string ErrorMessage { get; set; } = string.Empty;
    public string SuccessMessage { get; set; } = string.Empty;

    // Срабатывает при загрузке страницы
    public async Task<IActionResult> OnGetAsync()
    {
        await LoadUsersAsync();
        return Page();
    }

    // Срабатывает при отправке формы создания пользователя
    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUsername) || string.IsNullOrWhiteSpace(NewPassword))
        {
            ErrorMessage = "Имя пользователя и пароль не могут быть пустыми.";
            await LoadUsersAsync();
            return Page();
        }

        // Вызываем созданный ранее метод в UserService
        bool isCreated = await _userService.CreateUserAsync(NewUsername, NewPassword, NewRole);

        if (!isCreated)
        {
            ErrorMessage = $"Пользователь с именем '{NewUsername}' уже существует.";
            await LoadUsersAsync();
            return Page();
        }

        SuccessMessage = $"Пользователь '{NewUsername}' успешно создан!";

        // Очищаем поля формы после успешного создания
        NewUsername = string.Empty;
        NewPassword = string.Empty;

        await LoadUsersAsync();
        return Page();
    }

    // Срабатывает при удалении пользователя
    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user != null)
        {
            // Защита: не даем админу удалить самого себя
            var currentUserIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(currentUserIdClaim, out Guid currentUserId) && currentUserId == id)
            {
                ErrorMessage = "Вы не можете удалить свою собственную учетную запись.";
                await LoadUsersAsync();
                return Page();
            }

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();
            SuccessMessage = $"Пользователь '{user.Username}' успешно удален.";
        }

        await LoadUsersAsync();
        return Page();
    }

    private async Task LoadUsersAsync()
    {
        // Загружаем список пользователей вместе с количеством их задач
        ExistingUsers = await _context.Users
            .Include(u => u.Tasks)
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync();
    }
}