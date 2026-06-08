using System.Threading.Channels;

namespace KaraokePlatform.Services.Background;

public class QueueChannel
{
    // Ограничиваем очередь, например, максимум 100 задачами одновременно
    private readonly Channel<Guid> _queue;

    public QueueChannel()
    {
        var options = new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait // Если очередь забита, веб-поток подождет
        };
        _queue = Channel.CreateBounded<Guid>(options);
    }

    // Метод для веб-страницы: положить задачу в очередь
    public async ValueTask AddTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await _queue.Writer.WriteAsync(taskId, cancellationToken);
    }

    // Метод для воркера: читать задачи из очереди
    public IAsyncEnumerable<Guid> ReadTasksAsync(CancellationToken cancellationToken = default)
    {
        return _queue.Reader.ReadAllAsync(cancellationToken);
    }
}