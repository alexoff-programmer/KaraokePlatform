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
    private readonly TaskCancellationManager _cancellationManager;

    public VideoProcessingWorker(
        QueueChannel queueChannel,
        IServiceProvider serviceProvider,
        ILogger<VideoProcessingWorker> logger,
        IHubContext<NotificationHub> hubContext,
        TaskCancellationManager cancellationManager)
    {
        _queueChannel = queueChannel;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _hubContext = hubContext;
        _cancellationManager = cancellationManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Фоновый воркер обработки видео запущен.");

        await foreach (var taskId in _queueChannel.ReadTasksAsync(stoppingToken))
        {
            _logger.LogInformation($"Получена задача {taskId} из очереди.");

            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var transcriber = scope.ServiceProvider.GetRequiredService<WhisperTranscriber>();
            var renderer = scope.ServiceProvider.GetRequiredService<VideoRenderer>();
            var webHostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

            var task = await context.KaraokeTasks.FindAsync(new object[] { taskId }, stoppingToken);
            if (task == null) continue;

            // Четко смотрим текущий статус из базы
            bool isFirstPhase = task.Status == TaskStatus.InQueue;

            if (!isFirstPhase && task.Status != TaskStatus.ReadyToRender)
            {
                _logger.LogWarning($"Задача {task.Id} имеет некорректный статус {task.Status} для обработки. Пропускаем.");
                continue;
            }

            var user = await context.Users.FindAsync(task.UserId);
            string username = user?.Username ?? string.Empty;

            using var registration = _cancellationManager.RegisterTask(task.Id, stoppingToken, out var taskToken);

            try
            {
                taskToken.ThrowIfCancellationRequested();

                if (isFirstPhase)
                {
                    // =================================================================
                    // [ФАЗА 1] ИИ-ОБРАБОТКА (WhisperX / Demucs)
                    // =================================================================
                    task.Status = TaskStatus.Processing;
                    task.UpdatedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync(taskToken);

                    _logger.LogInformation($"[ФАЗА 1] Началась ИИ-обработка файла: {task.OriginalFileName}");

                    if (!string.IsNullOrEmpty(username))
                    {
                        await _hubContext.Clients.Group(username)
                            .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Processing: 10% (Инициализация...)", null, taskToken);
                    }

                    var fullAudioPathPhase1 = Path.Combine(webHostEnvironment.WebRootPath, task.AudioFilePath.TrimStart('\\', '/'));

                    var phrases = await transcriber.ProcessAudioToPhrasesAsync(task.Id, fullAudioPathPhase1, task.Language, task.SeparationQuality, async (progress) =>
                    {
                        using (var dbScope = _serviceProvider.CreateScope())
                        {
                            var db = dbScope.ServiceProvider.GetRequiredService<AppDbContext>();
                            var t = await db.KaraokeTasks.FindAsync(task.Id);
                            if (t != null)
                            {
                                t.Progress = progress;
                                await db.SaveChangesAsync();
                            }
                        }

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

                    _logger.LogInformation($"Текст для задачи {task.Id} успешно распознан и ожидает проверки.");
                    _cancellationManager.UnregisterTask(task.Id);
                    continue;
                }
                else
                {
                    // =================================================================
                    // [ФАЗА 2] ЧИСТЫЙ РЕНДЕРИНГ ВИДЕО (FFmpeg)
                    // =================================================================
                    _logger.LogInformation($"[ФАЗА 2] Запуск рендеринга видео для задачи: {task.Id}");

                    if (!string.IsNullOrEmpty(username))
                    {
                        await _hubContext.Clients.Group(username)
                            .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Processing: 60% (Сборка видео...)", null, taskToken);
                    }

                    // Save Phase 2 progress (60%)
                    task.Progress = 60;
                    await context.SaveChangesAsync(taskToken);

                    var outputFolder = Path.Combine(webHostEnvironment.WebRootPath, "output");
                    var assOutputPath = Path.Combine(outputFolder, $"{task.Id}.ass");
                    var videoFileName = $"{Guid.NewGuid()}.mp4";
                    var fullVideoOutputPath = Path.Combine(outputFolder, videoFileName);

                    string cleanAudioRelative = task.AudioFilePath.TrimStart('\\', '/');
                    string fullAudioPathPhase2 = Path.Combine(webHostEnvironment.WebRootPath, cleanAudioRelative);
                    string expectedInstrumentalPath = Path.Combine(outputFolder, $"{task.Id}_instrumental.wav");

                    if (task.RemoveVocal && File.Exists(expectedInstrumentalPath))
                    {
                        fullAudioPathPhase2 = expectedInstrumentalPath;
                    }

                    string? fullBgPath = !string.IsNullOrEmpty(task.BackgroundImagePath)
                        ? Path.Combine(webHostEnvironment.WebRootPath, task.BackgroundImagePath.TrimStart('\\', '/'))
                        : null;

                    // Передаем taskToken во внешний рендерер, чтобы можно было отменить FFmpeg
                    await renderer.RenderKaraokeVideoAsync(fullAudioPathPhase2, assOutputPath, fullVideoOutputPath, fullBgPath, task.VideoFormat);

                    taskToken.ThrowIfCancellationRequested();

                    task.Status = TaskStatus.Completed;
                    task.Progress = 100;
                    task.VideoFilePath = Path.Combine("output", videoFileName);
                    task.UpdatedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync(stoppingToken);

                    if (!string.IsNullOrEmpty(username))
                    {
                        await _hubContext.Clients.Group(username)
                            .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Completed", task.VideoFilePath.Replace("\\", "/"), stoppingToken);
                    }

                    _logger.LogInformation($"Успешно собрано видео для задачи: {task.Id}");
                    _cancellationManager.UnregisterTask(task.Id);
                }
            }
            catch (OperationCanceledException ex)
            {
                if (taskToken.IsCancellationRequested)
                {
                    _logger.LogWarning($"Обработка задачи {taskId} была принудительно прервана пользователем.");
                    _cancellationManager.UnregisterTask(taskId);
                }
                else
                {
                    _logger.LogError(ex, $"Обработка задачи {taskId} была прервана из-за таймаута или внутренней ошибки отмены.");
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при обработке задачи {taskId}. Фиксируем статус Failed в изолированном контексте.");
                _cancellationManager.UnregisterTask(taskId);

                // Спасаем воркер: создаем выделенный scope для записи ошибки
                try
                {
                    using var failScope = _serviceProvider.CreateScope();
                    var failContext = failScope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var failTask = await failContext.KaraokeTasks.FirstOrDefaultAsync(t => t.Id == taskId, stoppingToken);
                    if (failTask != null)
                    {
                        failTask.Status = TaskStatus.Failed;
                        failTask.ErrorMessage = ex.Message;
                        failTask.UpdatedAt = DateTime.UtcNow;
                        await failContext.SaveChangesAsync(stoppingToken);

                        if (!string.IsNullOrEmpty(username))
                        {
                            await _hubContext.Clients.Group(username)
                                .SendAsync("UpdateTaskStatus", failTask.Id.ToString(), "Failed", null, stoppingToken);
                        }
                    }
                }
                catch (Exception criticalEx)
                {
                    _logger.LogCritical(criticalEx, $"Не удалось записать статус Failed для задачи {taskId} в базу данных!");
                }
            }
        }
    }
}