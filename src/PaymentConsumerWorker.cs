using System.Buffers;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Channels;
using StackExchange.Redis;

namespace Rinha;

public class PaymentConsumerWorker : BackgroundService
{
    private readonly Channel<PaymentRequest> _queue;
    private readonly ILogger _logger;
    private readonly PaymentProcessorApi _defaultPaymentProcessor;
    private readonly PaymentProcessorApi _fallbackPaymentProcessor;
    private readonly IDatabase _db;

    public PaymentConsumerWorker(
        IConfiguration configuration,
        IDatabase db,
        Channel<PaymentRequest> queue,
        ILogger<PaymentConsumerWorker> logger
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
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                var payment = await _queue.Reader.ReadAsync(stoppingToken);
                payment.RequestedAt = DateTime.UtcNow;
                var timestamp = payment.RequestedAt.ToUnixTimeMilliseconds();

                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
                    payment,
                    AppJsonSerializerContext.Default.PaymentRequest
                );
                var result = await _defaultPaymentProcessor.PostAsync(jsonBytes, stoppingToken);
                if (result == PaymentResult.Success)
                {
                    await _db.SortedSetAddAsync("paymentsDefault", jsonBytes, timestamp);
                    continue;
                }

                result = await _fallbackPaymentProcessor.PostAsync(jsonBytes, stoppingToken);
                if (result == PaymentResult.Success)
                {
                    await _db.SortedSetAddAsync("paymentsFallback", jsonBytes, timestamp);
                    continue;
                }
                _logger.LogWarning(
                    "Payment {CorrelationId} failed with result {Result}.",
                    payment.CorrelationId,
                    result
                );

                await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
                await _queue.Writer.WriteAsync(payment, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }
}
