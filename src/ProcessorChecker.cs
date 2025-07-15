using StackExchange.Redis;

namespace Rinha;

public class ProcessorChecker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProcessorChecker> _logger;
    private const int MaxResponseTimeAllowed = 100; // in milliseconds

    public ProcessorChecker(IServiceProvider serviceProvider, ILogger<ProcessorChecker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Timed Hosted Service running.");

        // When the timer should have no due-time, then do the work once now.
        await ChooseBestProcessorAsync();

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ChooseBestProcessorAsync();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "An error occurred while checking the payment processors.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Timed Hosted Service is stopping.");
        }
    }

    private async Task ChooseBestProcessorAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDatabase>();
        var defaultPaymentProcessor =
            scope.ServiceProvider.GetRequiredKeyedService<PaymentProcessorApi>("default");
        var fallbackPaymentProcessor =
            scope.ServiceProvider.GetRequiredKeyedService<PaymentProcessorApi>("fallback");

        var defaultResult = await defaultPaymentProcessor.GetHealthAsync();
        var fallbackResult = await fallbackPaymentProcessor.GetHealthAsync();
        if (defaultResult is { Failing: false, MinResponseTime: <= MaxResponseTimeAllowed })
        {
            _logger.LogInformation("Default payment processor is health. Switching to default.");
            await db.StringSetAsync("best-processor", "default");
            return;
        }
        if (fallbackResult is { Failing: false, MinResponseTime: <= MaxResponseTimeAllowed })
        {
            _logger.LogInformation("Fallback payment processor is health. Switching to fallback.");
            await db.StringSetAsync("best-processor", "fallback");
            return;
        }

        _logger.LogWarning(
            "Both payment processors are unhealthy or delayed. best-processor will be set to none."
        );
        await db.StringSetAsync("best-processor", "none");
    }
}