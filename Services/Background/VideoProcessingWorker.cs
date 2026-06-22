using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KaraokePlatform.Data;
using KaraokePlatform.Hubs;
using KaraokePlatform.Services.Audio;
using KaraokePlatform.Services.Audio.Records;
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

    // 1. Добавь поле и инжект нового менеджера в конструктор воркера:
    // private readonly TaskCancellationManager _cancellationManager;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Фоновый воркер обработки видео запущен.");

        await foreach (var taskId in _queueChannel.ReadTasksAsync(stoppingToken))
        {
            _logger.LogInformation($"Получена задача {taskId} из очереди.");

            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transcriber = scope.ServiceProvider.GetRequiredService<WhisperTranscriber>();
            var cancellationManager = scope.ServiceProvider.GetRequiredService<TaskCancellationManager>();
            var webHostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

            var task = await context.KaraokeTasks.FindAsync(new object[] { taskId }, stoppingToken);
            if (task == null) continue;

            var user = await context.Users.FindAsync(task.UserId);
            string username = user?.Username ?? string.Empty;

            // 2. РЕГИСТРИРУЕМ ТОКЕН ОТМЕНЫ ДЛЯ ТЕКУЩЕЙ ЗАДАЧИ
            using var registration = cancellationManager.RegisterTask(task.Id, stoppingToken, out var taskToken);

            try
            {
                // Проверяем, не отменили ли задачу, пока она висела в очереди
                taskToken.ThrowIfCancellationRequested();

                task.Status = TaskStatus.Processing;
                task.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(taskToken); // Используем taskToken

                _logger.LogInformation($"Началась обработка файла: {task.OriginalFileName}");

                if (!string.IsNullOrEmpty(username))
                {
                    await _hubContext.Clients.Group(username)
                        .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Processing: 10% (Инициализация...)", null, taskToken);
                }

                var fullAudioPath = Path.Combine(webHostEnvironment.WebRootPath, task.AudioFilePath);

                _logger.LogInformation("Запуск нейросети Whisper для получения текстовых фраз...");

                // Передаем наш токен во внутренние методы ИИ-сепаратора и Whisper
                // (Убедись, что внутри ProcessAudioToPhrasesAsync этот токен прокидывается в WaitForExitAsync() или ProcessAsync)
                var phrases = await transcriber.ProcessAudioToPhrasesAsync(task.Id, fullAudioPath, task.Language, async (progress) =>
                {
                    if (!string.IsNullOrEmpty(username))
                    {
                        string statusText = $"Processing: {progress}% (Распознавание ИИ...)";
                        await _hubContext.Clients.Group(username).SendAsync("UpdateTaskStatus", task.Id.ToString(), statusText, null, taskToken);
                    }
                });

                taskToken.ThrowIfCancellationRequested();

                task.DetectedLinesJson = System.Text.Json.JsonSerializer.Serialize(phrases);
                task.Status = TaskStatus.AwaitingReview;
                task.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(taskToken);

                if (!string.IsNullOrEmpty(username))
                {
                    await _hubContext.Clients.Group(username)
                        .SendAsync("UpdateTaskStatus", task.Id.ToString(), "AwaitingReview", null, taskToken);
                }

                _logger.LogInformation($"Текст для задачи {task.Id} успешно распознан.");

                cancellationManager.UnregisterTask(task.Id); // Снимаем с учета при успехе
                continue;
            }
            catch (OperationCanceledException)
            {
                // Сюда мы прилетим, если пользователь нажал "Удалить" во время обработки
                _logger.LogWarning($"Обработка задачи {taskId} была принудительно прервана пользователем.");

                // Базу здесь не трогаем, так как бэкенд страницы её уже удалил
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при обработке задачи {taskId}");

                // Фикс: если база упала, но запись не была удалена юзером — меняем статус на Failed
                var checkTask = await context.KaraokeTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, stoppingToken);
                if (checkTask != null)
                {
                    task.Status = TaskStatus.Failed;
                    task.ErrorMessage = ex.Message;
                    task.UpdatedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync(stoppingToken);

                    if (!string.IsNullOrEmpty(username))
                    {
                        await _hubContext.Clients.Group(username).SendAsync("UpdateTaskStatus", task.Id.ToString(), "Failed", null, stoppingToken);
                    }
                }
            }
        }
    }
}