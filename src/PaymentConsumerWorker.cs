using System.Threading.Channels;
using StackExchange.Redis;

namespace Rinha;

public class PaymentConsumerWorker : BackgroundService
{
    private readonly Channel<PaymentRequest> _queue;
    private readonly PaymentProcessorApi _defaultPaymentProcessor;
    private readonly PaymentProcessorApi _fallbackPaymentProcessor;
    private readonly IDatabase _db;

    public PaymentConsumerWorker(
        IConfiguration configuration,
        IDatabase db,
        Channel<PaymentRequest> queue
    )
    {
        var defaultBaseUrl =
            configuration.GetValue<string>("PaymentProcessorDefault:BaseUrl")
            ?? throw new InvalidOperationException(
                "PaymentProcessorDefault:BaseUrl is not configured."
            );
        _defaultPaymentProcessor = new(defaultBaseUrl);
        var fallbackBaseUrl =
            configuration.GetValue<string>("PaymentProcessorFallback:BaseUrl")
            ?? throw new InvalidOperationException(
                "PaymentProcessorFallback:BaseUrl is not configured."
            );
        _fallbackPaymentProcessor = new(fallbackBaseUrl);

        _db = db;
        _queue = queue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (stoppingToken.IsCancellationRequested == false)
            {
                var payment = await _queue.Reader.ReadAsync(stoppingToken);
                await ProcessItemAsync(payment, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessItemAsync(PaymentRequest payment, CancellationToken stoppingToken)
    {
        var result = await _defaultPaymentProcessor.PostAsync(payment, stoppingToken);
        if (result == PaymentResult.Success)
        {
            await _db.SavePaymentAsync(payment, Processor.Default);
            return;
        }

        result = await _fallbackPaymentProcessor.PostAsync(payment, stoppingToken);
        if (result == PaymentResult.Success)
        {
            await _db.SavePaymentAsync(payment, Processor.Fallback);
            return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(10), stoppingToken);
        await _queue.Writer.WriteAsync(payment, stoppingToken);
    }
}
