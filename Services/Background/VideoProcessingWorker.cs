using KaraokePlatform.Data;
using KaraokePlatform.Hubs;
using KaraokePlatform.Services.Audio;
using KaraokePlatform.Services.Video;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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

        // Читаем задачи по мере их поступления в канал
        await foreach (var taskId in _queueChannel.ReadTasksAsync(stoppingToken))
        {
            _logger.LogInformation($"Получена задача {taskId} из очереди.");

            // Область видимости (Scope) нужна, так как AppDbContext является Scoped-сервисом
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transcriber = scope.ServiceProvider.GetRequiredService<WhisperTranscriber>();
            var renderer = scope.ServiceProvider.GetRequiredService<VideoRenderer>();

            var task = await context.KaraokeTasks.FindAsync(new object[] { taskId }, stoppingToken);
            if (task == null) continue;
            var user = await context.Users.FindAsync(task.UserId);
            string username = user?.Username ?? string.Empty;

            try
            {
                // 1. Меняем статус на "В процессе обработки"
                task.Status = TaskStatus.Processing;
                task.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);

                _logger.LogInformation($"Началась обработка файла: {task.OriginalFileName}");

                // ОТПРАВКА: Оповещаем фронтенд, что видео начало обрабатываться
                if (!string.IsNullOrEmpty(username))
                {
                    await _hubContext.Clients.User(username)
                        .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Processing", stoppingToken);
                }

                // Полный физический путь к загруженному аудио
                var fullAudioPath = Path.Combine(scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>().WebRootPath, task.AudioFilePath);
                var outputFolder = Path.Combine(scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>().WebRootPath, "output");

                if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

                _logger.LogInformation("Запуск нейросети Whisper для распознавания текста...");

                // Генерируем субтитры .ASS через Whisper
                string assSubtitlesPath = await transcriber.TranscribeToAssAsync(fullAudioPath, outputFolder, task.Language);

                _logger.LogInformation($"Субтитры успешно созданы: {assSubtitlesPath}");

                // Имя для готового видеоролика
                var videoFileName = $"{Guid.NewGuid()}.mp4";
                var fullVideoOutputPath = Path.Combine(outputFolder, videoFileName);

                // ЗАПУСКАЕМ СБОРКУ ВИДЕО
                await renderer.RenderKaraokeVideoAsync(fullAudioPath, assSubtitlesPath, fullVideoOutputPath);

                // Записываем в базу относительный путь к готовому файлу для скачивания с фронтенда
                task.Status = TaskStatus.Completed;
                task.VideoFilePath = Path.Combine("output", videoFileName); // Относительный путь для браузера
                task.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);

                // ОТПРАВКА: Оповещаем фронтенд, что всё готово, и передаем ссылку на скачивание
                if (!string.IsNullOrEmpty(username))
                {
                    await _hubContext.Clients.User(username)
                        .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Completed", task.VideoFilePath, stoppingToken);
                }

                _logger.LogInformation($"Успешно обработано: {task.OriginalFileName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при обработке задачи {taskId}");
                task.Status = TaskStatus.Failed;
                task.ErrorMessage = ex.Message;
                task.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);

                // ОТПРАВКА: Оповещаем об ошибке
                if (!string.IsNullOrEmpty(username))
                {
                    await _hubContext.Clients.User(username)
                        .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Failed", stoppingToken);
                }
            }

            await context.SaveChangesAsync(stoppingToken);
        }
    }
}