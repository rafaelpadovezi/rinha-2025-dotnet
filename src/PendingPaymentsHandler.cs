using System.Text.Json;
using StackExchange.Redis;

namespace Rinha;

public class PendingPaymentsHandler : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PendingPaymentsHandler> _logger;

    public PendingPaymentsHandler(
        IServiceProvider serviceProvider,
        ILogger<PendingPaymentsHandler> logger
    )
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const int maxConcurrentRequests = 20;
        var concurrentTaskExecuter = new ConcurrentTaskExecutor(maxConcurrentRequests);
        while (true)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IDatabase>();
            var paymentService = scope.ServiceProvider.GetRequiredService<PaymentService>();

            var pendingPayments = await db.ListLeftPopAsync(
                "pendingPayments",
                maxConcurrentRequests
            );
            if (pendingPayments is null || pendingPayments.Length == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken); // Wait for the next iteration
                continue;
            }
            var tasks = new List<Func<Task>>();
            foreach (var pendingPaymentJson in pendingPayments)
            {
                Func<Task> task = async () =>
                {
                    var pendingPayment = JsonSerializer.Deserialize<PaymentEvent>(
                        pendingPaymentJson
                    );
                    var payment = new PaymentApiRequest(
                        pendingPayment.CorrelationId,
                        pendingPayment.Amount,
                        pendingPayment.RequestedAt
                    );

                    var bestProcessor = await db.StringGetAsync("best-processor");
                    if (bestProcessor == "default")
                    {
                        await paymentService.SendDefaultPaymentsAsync(payment);
                    }

                    if (bestProcessor == "fallback")
                    {
                        await paymentService.SendFallbackPaymentsAsync(payment);
                    }

                    if (bestProcessor == "none")
                    {
                        await db.SavePendingPaymentAsync(payment, "none");
                    }
                };
                tasks.Add(task);
            }

            await concurrentTaskExecuter.ExecuteAllAsync(tasks, null, stoppingToken);
        }
    }
}
