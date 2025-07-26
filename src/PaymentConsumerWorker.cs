using System.Threading.Channels;

using StackExchange.Redis;

namespace Rinha;

public class PaymentConsumerWorker : BackgroundService
{
    private readonly Channel<PaymentApiRequest> _queue;
    private readonly PaymentProcessorApi _defaultPaymentProcessor;
    private readonly PaymentProcessorApi _fallbackPaymentProcessor;
    private readonly IDatabase _db;
    private readonly SemaphoreSlim _semaphore = new(20, 20);

    public PaymentConsumerWorker(IConfiguration configuration, IDatabase db, Channel<PaymentApiRequest> queue, ILogger<PaymentConsumerWorker> logger)
    {
        var defaultBaseUrl =
            configuration.GetValue<string>("PaymentProcessorDefault:BaseUrl")
            ?? throw new InvalidOperationException("PaymentProcessorDefault:BaseUrl is not configured.");
        _defaultPaymentProcessor = new PaymentProcessorApi(defaultBaseUrl);
        var fallbackBaseUrl =
            configuration.GetValue<string>("PaymentProcessorFallback:BaseUrl")
            ?? throw new InvalidOperationException("PaymentProcessorFallback:BaseUrl is not configured.");
        _fallbackPaymentProcessor = new PaymentProcessorApi(fallbackBaseUrl);
        _db = db;
        _queue = queue;
    }

    public async ValueTask EnqueueAsync(PaymentApiRequest payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        await _queue.Writer.WriteAsync(payment);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>();
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_queue.Reader.TryRead(out var payment))
                {
                    await _semaphore.WaitAsync(stoppingToken);
                    var task = ProcessItemAsync(payment, stoppingToken);
                    tasks.Add(task);
                    tasks.RemoveAll(x => x.IsCompleted);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Processing cancelled");
        }
        finally
        {
            // Wait for all remaining tasks to complete
            await Task.WhenAll(tasks);
        }
    }

    private async Task ProcessItemAsync(PaymentApiRequest payment, CancellationToken stoppingToken)
    {
        try
        {
            var result = await _defaultPaymentProcessor.PostAsync(payment, stoppingToken);
            if (result == PaymentResult.Success)
            {
                await _db.SavePaymentAsync(payment, "default");
                return;
            }

            result = await _fallbackPaymentProcessor.PostAsync(payment, stoppingToken);
            if (result == PaymentResult.Success)
            {
                await _db.SavePaymentAsync(payment, "fallback");
                return;
            }

            await _queue.Writer.WriteAsync(payment, stoppingToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}