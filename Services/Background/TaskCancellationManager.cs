using System;
using System.Collections.Concurrent;
using System.Threading;

namespace KaraokePlatform.Services.Background;

public class TaskCancellationManager
{
    // Храним токены отмены: Ключ — TaskId, Значение — CancellationTokenSource
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeTokens = new();

    // Регистрируем новую задачу в обработке
    public CancellationTokenRegistration RegisterTask(Guid taskId, CancellationToken workerToken, out CancellationToken combinedToken)
    {
        var cts = new CancellationTokenSource();
        _activeTokens[taskId] = cts;

        // Связываем токен отмены воркера (при остановке сайта) и токен отмены конкретной задачи
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(workerToken, cts.Token);
        combinedToken = linkedCts.Token;

        // Автоматически удаляем из словаря по завершении
        return combinedToken.Register(() => _activeTokens.TryRemove(taskId, out _));
    }

    // Метод отмены задачи
    public bool CancelTask(Guid taskId)
    {
        if (_activeTokens.TryRemove(taskId, out var cts))
        {
            try
            {
                cts.Cancel(); // Вызываем отмену
                cts.Dispose();
                return true;
            }
            catch { }
        }
        return false;
    }

    // Удаление из списка без отмены (при успешном завершении)
    public void UnregisterTask(Guid taskId)
    {
        if (_activeTokens.TryRemove(taskId, out var cts))
        {
            cts.Dispose();
        }
    }
}