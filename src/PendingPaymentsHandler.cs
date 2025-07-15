using System.Text.Json;

using StackExchange.Redis;

namespace Rinha;

public class PendingPaymentsHandler : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PendingPaymentsHandler> _logger;

    public PendingPaymentsHandler(IServiceProvider serviceProvider, ILogger<PendingPaymentsHandler> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), stoppingToken); // Wait for the next iteration
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IDatabase>();
            var defaultPaymentProcessor =
                scope.ServiceProvider.GetRequiredKeyedService<PaymentProcessorApi>("default");
            var fallbackPaymentProcessor =
                scope.ServiceProvider.GetRequiredKeyedService<PaymentProcessorApi>("fallback");
            var pendingPayments = await db.ListLeftPopAsync("pendingPayments", 20);
            if (pendingPayments is null || pendingPayments.Length == 0)
            {
                // No pending payments to process
                continue;
            }
            var tasks = new List<Task>();
            foreach (var pendingPaymentJson in pendingPayments)
            {
                var task = Task.Run(async () =>
                {
                    var pendingPayment = JsonSerializer.Deserialize<PaymentEvent>(pendingPaymentJson);
                    var paymentApiRequest = new PaymentApiRequest(
                        pendingPayment.CorrelationId, pendingPayment.Amount, pendingPayment.RequestedAt
                    );
                    if (pendingPayment.Processor is "default" or "none")
                    {
                        var success = await defaultPaymentProcessor.PostIfNotExistAsync(paymentApiRequest);
                        if (!success)
                        {
                            await db.ListLeftPushAsync("pendingPayments", pendingPayments);
                            _logger.LogWarning(
                                "PendingPaymentsHandler: Default payment processor failed for correlation ID {CorrelationId}",
                                pendingPayment.CorrelationId
                            );
                        }
                        else
                        {
                            await SavePaymentAsync(paymentApiRequest, "default");
                            _logger.LogInformation(
                                "PendingPaymentsHandler: Payment processed successfully by default processor for correlation ID {CorrelationId}",
                                pendingPayment.CorrelationId
                            );
                        }
                    }
                    else if (pendingPayment.Processor == "fallback")
                    {
                        var success = await fallbackPaymentProcessor.PostIfNotExistAsync(new(
                            pendingPayment.CorrelationId, pendingPayment.Amount, pendingPayment.RequestedAt
                        ));
                        if (!success)
                        {
                            await db.ListLeftPushAsync("pendingPayments", pendingPayments);
                            _logger.LogWarning(
                                "PendingPaymentsHandler: Fallback payment processor failed for correlation ID {CorrelationId}",
                                pendingPayment.CorrelationId
                            );
                        }
                        else
                        {
                            await SavePaymentAsync(paymentApiRequest, "fallback");
                            _logger.LogInformation(
                                "PendingPaymentsHandler: Payment processed successfully by fallback processor for correlation ID {CorrelationId}",
                                pendingPayment.CorrelationId
                            );
                        }
                    }
                });
                tasks.Add(task);
            }

            await Task.WhenAll(tasks);

            continue;

            async Task SavePaymentAsync(PaymentApiRequest paymentApiRequest, string processor)
            {
                var json = JsonSerializer.Serialize(new PaymentEvent(
                    paymentApiRequest.CorrelationId, paymentApiRequest.Amount, paymentApiRequest.RequestedAt, processor, "success"
                ));
                var timestamp = paymentApiRequest.RequestedAt.ToUnixTimeSeconds();
                await db.SortedSetAddAsync("payments", json, timestamp);
            }
        }
    }
}