using KaraokePlatform.Data;
using KaraokePlatform.Services.Audio;
using Microsoft.EntityFrameworkCore;
using TaskStatus = KaraokePlatform.Data.Entities.KaraokeTaskStatus;

namespace KaraokePlatform.Services.Background;

public class VideoProcessingWorker : BackgroundService
{
    private readonly QueueChannel _queueChannel;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VideoProcessingWorker> _logger;

    public VideoProcessingWorker(
        QueueChannel queueChannel,
        IServiceProvider serviceProvider,
        ILogger<VideoProcessingWorker> logger)
    {
        _queueChannel = queueChannel;
        _serviceProvider = serviceProvider;
        _logger = logger;
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

            var task = await context.KaraokeTasks.FindAsync(new object[] { taskId }, stoppingToken);
            if (task == null) continue;

            try
            {
                // 1. Меняем статус на "В процессе обработки"
                task.Status = TaskStatus.Processing;
                task.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);

                _logger.LogInformation($"Началась обработка файла: {task.OriginalFileName}");

                // Полный физический путь к загруженному аудио
                var fullAudioPath = Path.Combine(scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>().WebRootPath, task.AudioFilePath);
                var outputFolder = Path.Combine(scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>().WebRootPath, "output");

                if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

                _logger.LogInformation("Запуск нейросети Whisper для распознавания текста...");

                // Генерируем субтитры .ASS через Whisper
                string assSubtitlesPath = await transcriber.TranscribeToAssAsync(fullAudioPath, outputFolder, task.Language);

                _logger.LogInformation($"Субтитры успешно созданы: {assSubtitlesPath}");

                // Временно оставляем имитацию видео, используя сгенерированные субтитры
                await Task.Delay(2000, stoppingToken);

                // Имитируем успешное завершение и прописываем путь к будущему видео
                task.Status = TaskStatus.Completed;
                task.VideoFilePath = task.AudioFilePath.Replace(".mp3", ".mp4"); // Временно для теста
                task.UpdatedAt = DateTime.UtcNow;

                _logger.LogInformation($"Успешно обработано: {task.OriginalFileName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка при обработке задачи {taskId}");
                task.Status = TaskStatus.Failed;
                task.ErrorMessage = ex.Message;
                task.UpdatedAt = DateTime.UtcNow;
            }

            await context.SaveChangesAsync(stoppingToken);
        }
    }
}