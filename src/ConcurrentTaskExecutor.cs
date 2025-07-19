namespace Rinha;

public sealed class ConcurrentTaskExecutor(int maxConcurrency) : IDisposable
{
    private readonly int _maxConcurrency = maxConcurrency;
    private readonly SemaphoreSlim _semaphore = new(maxConcurrency, maxConcurrency);

    public async Task ExecuteAllAsync<T>(
        IEnumerable<Func<Task<T>>> taskFactories,
        Action<T>? onTaskCompleted = null,
        CancellationToken cancellationToken = default
    )
    {
        var tasks = taskFactories.Select(factory =>
            ExecuteWithSemaphore(factory, onTaskCompleted, cancellationToken)
        );
        await Task.WhenAll(tasks);
    }

    public async Task ExecuteAllAsync(
        IEnumerable<Func<Task>> taskFactories,
        Action? onTaskCompleted = null,
        CancellationToken cancellationToken = default
    )
    {
        var tasks = taskFactories.Select(factory =>
            ExecuteWithSemaphore(factory, onTaskCompleted, cancellationToken)
        );
        await Task.WhenAll(tasks);
    }

    private async Task<T> ExecuteWithSemaphore<T>(
        Func<Task<T>> taskFactory,
        Action<T> onTaskCompleted,
        CancellationToken cancellationToken
    )
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var result = await taskFactory();
            onTaskCompleted?.Invoke(result);
            return result;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task ExecuteWithSemaphore(
        Func<Task> taskFactory,
        Action onTaskCompleted,
        CancellationToken cancellationToken
    )
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            await taskFactory();
            onTaskCompleted?.Invoke();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _semaphore?.Dispose();
    }
}
