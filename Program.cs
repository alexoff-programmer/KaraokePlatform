using KaraokePlatform.Data;
using KaraokePlatform.Services;
using KaraokePlatform.Services.Background;
using KaraokePlatform.Services.Audio;
using KaraokePlatform.Services.Audio.Interfaces;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using KaraokePlatform.Services.Video;
using KaraokePlatform.Hubs;
using KaraokePlatform.Settings;


var builder = WebApplication.CreateBuilder(args);

var instancePath = Path.Combine(builder.Environment.ContentRootPath, "instance");
if (!Directory.Exists(instancePath))
{
    Directory.CreateDirectory(instancePath);
}


// Регистрация базы данных SQLite
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Настройка аутентификации через Cookies
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Index"; // Куда перенаправлять, если пользователь не авторизован
        options.AccessDeniedPath = "/Index"; // Куда перенаправлять, если не хватает прав (например, не админ)
        options.ExpireTimeSpan = TimeSpan.FromDays(7); // Время жизни куки
    });
// Регистрация бизнес-сервиса
builder.Services.AddScoped<UserService>();

// РЕГИСТРАЦИЯ СЕРВИСОВ ДЛЯ ОБРАБОТКИ АУДИО
builder.Services.Configure<WhisperSettings>(builder.Configuration.GetSection("WhisperSettings"));
builder.Services.AddScoped<IAudioProcessor, AudioProcessor>();
builder.Services.AddScoped<ISpeechRecognizer, WhisperRecognizer>();
builder.Services.AddScoped<ISubtitleGenerator, AssSubtitleGenerator>();
builder.Services.AddScoped<WhisperTranscriber>();


// РЕГИСТРАЦИЯ СЕРВИСОВ ДЛЯ ОБРАБОТКИ ВИДЕО
builder.Services.AddScoped<VideoRenderer>();

// РЕГИСТРАЦИЯ КОНВЕЙЕРА ОБРАБОТКИ
builder.Services.AddSingleton<QueueChannel>(); // Очередь должна быть одна на всё приложение
builder.Services.AddSingleton<TaskCancellationManager>();
builder.Services.AddHostedService<VideoProcessingWorker>(); // Запуск фонового процесса

builder.Services.AddRazorPages(options =>
{
    // По желанию можно глобально закрыть весь сайт, кроме главной страницы входа:
    options.Conventions.AuthorizeFolder("/Admin");
    options.Conventions.AuthorizePage("/Dashboard");
});

builder.Services.AddSignalR();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    context.Database.OpenConnection();
    using var command = context.Database.GetDbConnection().CreateCommand();
    command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
    command.ExecuteNonQuery();
}

// Конфигурация HTTP-конвейера
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapHub<NotificationHub>("/notificationHub");

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<AppDbContext>();

    try
    {
        context.Database.EnsureCreated();
        // Check if new columns exist
        _ = context.KaraokeTasks.Select(t => new { t.GeminiApiKey, t.AutoImproveEnabled, t.SubtitleFont, t.FillStyle, t.PrimaryColor, t.SecondaryColor, t.VideoFormat, t.Progress }).FirstOrDefault();
    }
    catch (Exception)
    {
        // Recreate database if schema is outdated
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();
    }
    // Проверяем, есть ли вообще пользователи в базе
    if (!context.Users.Any())
    {
        var userService = services.GetRequiredService<UserService>();
        // Создаем дефолтного админа. Обязательно смени пароль при деплое!
        await userService.CreateUserAsync("admin", "admin123", KaraokePlatform.Data.Entities.Role.Admin);
    }
}

app.Run();


