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

    private static readonly SemaphoreSlim _concurrencySemaphore = new SemaphoreSlim(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Фоновый воркер обработки видео запущен.");

        await foreach (var taskId in _queueChannel.ReadTasksAsync(stoppingToken))
        {
            if (stoppingToken.IsCancellationRequested) break;

            await _concurrencySemaphore.WaitAsync(CancellationToken.None);

            try
            {
                _logger.LogInformation($"Получена задача {taskId} из очереди.");

                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var transcriber = scope.ServiceProvider.GetRequiredService<WhisperTranscriber>();
                var renderer = scope.ServiceProvider.GetRequiredService<VideoRenderer>();
                var webHostEnvironment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

                var task = await context.KaraokeTasks.FindAsync(new object[] { taskId }, CancellationToken.None);
                if (task == null) continue;

                bool isFirstPhase = task.Status == TaskStatus.InQueue;

                if (!isFirstPhase && task.Status != TaskStatus.ReadyToRender)
                {
                    _logger.LogWarning($"Задача {task.Id} имеет некорректный статус {task.Status} для обработки. Пропускаем.");
                    continue;
                }

                var user = await context.Users.FindAsync(task.UserId);
                string username = user?.Username ?? string.Empty;

                // Регистрируем задачу в глобальном менеджере отмены
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
                                    await db.SaveChangesAsync(CancellationToken.None);
                                }
                            }

                            if (!string.IsNullOrEmpty(username))
                            {
                                string statusText = $"Processing: {progress}% (Распознавание ИИ...)";
                                await _hubContext.Clients.Group(username).SendAsync("UpdateTaskStatus", task.Id.ToString(), statusText, null, CancellationToken.None);
                            }
                        });

                        taskToken.ThrowIfCancellationRequested();

                        task.DetectedLinesJson = System.Text.Json.JsonSerializer.Serialize(phrases);
                        task.Status = TaskStatus.AwaitingReview;
                        task.UpdatedAt = DateTime.UtcNow;
                        await context.SaveChangesAsync(CancellationToken.None);

                        if (!string.IsNullOrEmpty(username))
                        {
                            await _hubContext.Clients.Group(username)
                                .SendAsync("UpdateTaskStatus", task.Id.ToString(), "AwaitingReview", null, CancellationToken.None);
                        }

                        _logger.LogInformation($"Текст для задачи {task.Id} успешно распознан и ожидает проверки.");
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
                                .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Processing: 60% (Сборка видео...)", null, CancellationToken.None);
                        }

                        task.Progress = 60;
                        await context.SaveChangesAsync(CancellationToken.None);

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

                        await renderer.RenderKaraokeVideoAsync(fullAudioPathPhase2, assOutputPath, fullVideoOutputPath, fullBgPath, task.VideoFormat);

                        taskToken.ThrowIfCancellationRequested();

                        task.Status = TaskStatus.Completed;
                        task.Progress = 100;
                        task.VideoFilePath = Path.Combine("output", videoFileName);
                        task.UpdatedAt = DateTime.UtcNow;
                        await context.SaveChangesAsync(CancellationToken.None);

                        if (!string.IsNullOrEmpty(username))
                        {
                            await _hubContext.Clients.Group(username)
                                .SendAsync("UpdateTaskStatus", task.Id.ToString(), "Completed", task.VideoFilePath.Replace("\\", "/"), CancellationToken.None);
                        }

                        _logger.LogInformation($"Успешно собрано видео для задачи: {task.Id}");
                    }
                }
                catch (OperationCanceledException ex)
                {
                    if (taskToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogWarning($"Обработка задачи {taskId} была принудительно прервана пользователем.");
                        try
                        {
                            task.Status = isFirstPhase ? TaskStatus.InQueue : TaskStatus.ReadyToRender;
                            task.UpdatedAt = DateTime.UtcNow;
                            await context.SaveChangesAsync(CancellationToken.None);
                        }
                        catch { }
                    }
                    else
                    {
                        throw; // Принудительный стоп самого хоста приложения
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Ошибка при обработке задачи {taskId}. Фиксируем статус Failed в изолированном контексте.");

                    try
                    {
                        using var failScope = _serviceProvider.CreateScope();
                        var failContext = failScope.ServiceProvider.GetRequiredService<AppDbContext>();

                        var failTask = await failContext.KaraokeTasks.FirstOrDefaultAsync(t => t.Id == taskId, CancellationToken.None);
                        if (failTask != null)
                        {
                            failTask.Status = TaskStatus.Failed;
                            failTask.ErrorMessage = ex.Message;
                            failTask.UpdatedAt = DateTime.UtcNow;
                            await failContext.SaveChangesAsync(CancellationToken.None);

                            if (!string.IsNullOrEmpty(username))
                            {
                                await _hubContext.Clients.Group(username)
                                    .SendAsync("UpdateTaskStatus", failTask.Id.ToString(), "Failed", null, CancellationToken.None);
                            }
                        }
                    }
                    catch (Exception criticalEx)
                    {
                        _logger.LogCritical(criticalEx, $"Не удалось записать статус Failed для задачи {taskId} в базу данных!");
                    }
                }
            }
            finally
            {
                _cancellationManager.UnregisterTask(taskId);
                _concurrencySemaphore.Release();
            }
        }
    }
}