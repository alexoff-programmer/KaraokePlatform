using System.Diagnostics;
using KaraokePlatform.Data;
using KaraokePlatform.Hubs;
using KaraokePlatform.Services.Audio;
using KaraokePlatform.Services.Video;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskStatus = KaraokePlatform.Data.Entities.KaraokeTaskStatus;

namespace KaraokePlatform.Services.Background;

public class VideoProcessingWorker : BackgroundService
{
    private readonly QueueChannel _queueChannel;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VideoProcessingWorker> _logger;
    private readonly IHubContext<NotificationHub> _hubContext;

    public VideoProcessingWorker(
        QueueChannel queueChannel,
        IServiceProvider serviceProvider,
        ILogger<VideoProcessingWorker> logger,
        IHubContext<NotificationHub> hubContext)
    {
        _queueChannel = queueChannel;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _hubContext = hubContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Фоновый воркер обработки видео запущен.");

        // Бесконечно читаем задачи из очереди, пока приложение работает
        await foreach (var taskId in _queueChannel.ReadTasksAsync(stoppingToken))
        {
            _logger.LogInformation($"Получена задача {taskId} из очереди.");

            // Создаем временную область видимости для работы со Scoped-сервисами (БД, Whisper, Рендерер)
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transcriber = scope.ServiceProvider.GetRequiredService<WhisperTranscriber>();
            var renderer = scope.ServiceProvider.GetRequiredService<VideoRenderer>();
            var webHostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

            // Загружаем задачу из базы данных
            var task = await context.KaraokeTasks.FindAsync(new object[] { taskId }, stoppingToken);
            if (task == null) continue;

            // Ищем автора задачи для отправки персональных уведомлений через SignalR
            var user = await context.Users.FindAsync(task.UserId);
            string username = user?.Username ?? string.Empty;

            try
            {
                // Переводим задачу в статус "В процессе" и сохраняем в БД
                task.Status = TaskStatus.Processing;
                task.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);

                _logger.LogInformation($"Началась обработка файла: {task.OriginalFileName}");

                // Оповещаем фронтенд о старте (10% прогресса)
                if (!string.IsNullOrEmpty(username))
                {
                    await _hubContext.Clients.Group(username)
                        .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Processing: 10% (Инициализация...)", null, stoppingToken);
                }

                // Собираем полные пути к файлам на сервере
                var fullAudioPath = Path.Combine(webHostEnvironment.WebRootPath, task.AudioFilePath);
                var outputFolder = Path.Combine(webHostEnvironment.WebRootPath, "output");

                // Создаем папку output, если её еще нет на диске
                if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

                // Если в задаче есть картинка фона, собираем к ней полный путь. Иначе передаем null (будет черный фон)
                string? fullBgPath = !string.IsNullOrEmpty(task.BackgroundImagePath)
                    ? Path.Combine(webHostEnvironment.WebRootPath, task.BackgroundImagePath)
                    : null;

                _logger.LogInformation("Запуск нейросети Whisper для распознавания текста...");

                // ЭТАП 1: Генерируем караоке-субтитры (.ass) из аудиофайла
                string assSubtitlesPath = await transcriber.ProcessAudioAsync(
                    fullAudioPath, outputFolder, task.Language, async (progress) =>
                    {
                        // Транслируем текущий процент распознавания текста на фронтенд в реальном времени
                        if (!string.IsNullOrEmpty(username))
                        {
                            string statusText = $"Processing: {progress}% (Распознавание текста...)";
                            await _hubContext.Clients.Group(username)
                                .SendAsync("UpdateTaskStatus", task.Id.ToString(), statusText, null, stoppingToken);
                        }
                    });

                _logger.LogInformation($"Субтитры успешно созданы: {assSubtitlesPath}");

                // Оповещаем фронтенд о переходе к рендерингу видео (60% прогресса)
                if (!string.IsNullOrEmpty(username))
                {
                    await _hubContext.Clients.Group(username)
                        .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Processing: 60% (Сборка видео...)", null, stoppingToken);
                }

                // Генерируем уникальное имя для готового видеоролика
                var videoFileName = $"{Guid.NewGuid()}.mp4";
                var fullVideoOutputPath = Path.Combine(outputFolder, videoFileName);

                // ЭТАП 2: Запускаем рендеринг через FFmpeg (передаем аудио, субтитры, путь сохранения и фон)
                await renderer.RenderKaraokeVideoAsync(fullAudioPath, assSubtitlesPath, fullVideoOutputPath, fullBgPath);

                // ЭТАП 3: Фиксируем успешное завершение в базе данных
                task.Status = TaskStatus.Completed;
                task.VideoFilePath = Path.Combine("output", videoFileName);
                task.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);

                // Отправляем финальное уведомление на фронтенд вместе с веб-ссылкой на готовый MP4
                if (!string.IsNullOrEmpty(username))
                {
                    await _hubContext.Clients.Group(username)
                        .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Completed", task.VideoFilePath.Replace("\\", "/"), stoppingToken);
                }

                _logger.LogInformation($"Успешно обработано: {task.OriginalFileName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при обработке задачи {taskId}");

                // Если произошел сбой — сохраняем ошибку в БД и меняем статус задачи
                task.Status = TaskStatus.Failed;
                task.ErrorMessage = ex.Message;
                task.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);

                // Отправляем статус "Failed" на фронтенд, чтобы интерфейс убрал спиннер загрузки
                if (!string.IsNullOrEmpty(username))
                {
                    await _hubContext.Clients.Group(username)
                        .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Failed", null, stoppingToken);
                }
            }
        }
    }
}