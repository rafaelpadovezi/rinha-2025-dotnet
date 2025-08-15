using System.Threading.Channels;
using StackExchange.Redis;

namespace Rinha;

public class PaymentConsumerWorker : BackgroundService
{
    private readonly Channel<PaymentRequest> _queue;
    private readonly ILogger<PaymentConsumerWorker> _logger;
    private readonly PaymentProcessorApi _defaultPaymentProcessor;
    private readonly PaymentProcessorApi _fallbackPaymentProcessor;
    private readonly IDatabase _db;

    public PaymentConsumerWorker(
        IConfiguration configuration,
        IDatabase db,
        Channel<PaymentRequest> queue,
        ILogger<PaymentConsumerWorker> logger,
        IHttpClientFactory httpClientFactory
    )
    {
        var defaultBaseUrl =
            configuration.GetValue<string>("PaymentProcessorDefault:BaseUrl")
            ?? throw new InvalidOperationException(
                "PaymentProcessorDefault:BaseUrl is not configured."
            );
        _defaultPaymentProcessor = new(defaultBaseUrl, httpClientFactory);
        var fallbackBaseUrl =
            configuration.GetValue<string>("PaymentProcessorFallback:BaseUrl")
            ?? throw new InvalidOperationException(
                "PaymentProcessorFallback:BaseUrl is not configured."
            );
        _fallbackPaymentProcessor = new(fallbackBaseUrl, httpClientFactory);

        _db = db;
        _queue = queue;
        _logger = logger;
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

        _logger.LogWarning("Payment processing failed: {PaymentResult}", result);

        await Task.Delay(TimeSpan.FromMilliseconds(10), stoppingToken);
        await _queue.Writer.WriteAsync(payment, stoppingToken);
    }
}
