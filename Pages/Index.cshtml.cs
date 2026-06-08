using System.Security.Claims;
using KaraokePlatform.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KaraokePlatform.Pages;

public class IndexModel : PageModel
{
    private readonly UserService _userService;

    public IndexModel(UserService userService)
    {
        _userService = userService;
    }

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string ErrorMessage { get; set; } = string.Empty;

    public string SuccessMessage { get; set; } = string.Empty;

    public IActionResult OnGet()
    {
        // Если пользователь уже залогинен, не показываем форму, а сразу кидаем в Dashboard
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Dashboard");
        }

        SuccessMessage = TempData["SuccessMessage"] as string ?? string.Empty;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Пожалуйста, заполните все поля.";
            return Page();
        }

        // Проверяем пользователя через наш сервис в БД
        var user = await _userService.AuthenticateAsync(Username, Password);

        if (user == null)
        {
            ErrorMessage = "Неверное имя пользователя или пароль.";
            return Page();
        }

        // Формируем список "утверждений" (Claims) о пользователе для записи в Cookie
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString()) // Роль: Admin или User
        };

        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = true, // Запомнить браузер
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
        };

        // Непосредственно осуществляем вход (запись зашифрованной куки в браузер)
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(claimsIdentity),
            authProperties);

        // Перенаправляем в личный кабинет
        return RedirectToPage("/Dashboard");
    }
}