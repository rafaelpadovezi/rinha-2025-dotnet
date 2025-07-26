using StackExchange.Redis;

namespace Rinha;

public class PaymentService(
    [FromKeyedServices("default")] PaymentProcessorApi defaultPaymentProcessor,
    [FromKeyedServices("fallback")] PaymentProcessorApi fallbackPaymentProcessor,
    IDatabase db,
    ILogger<PaymentService> logger
)
{
    public Task SendDefaultPaymentsAsync(
        PaymentApiRequest payment,
        CancellationToken cancellationToken = default
    ) => SendPaymentsAsync(payment, "default", cancellationToken);

    public Task SendFallbackPaymentsAsync(
        PaymentApiRequest payment,
        CancellationToken cancellationToken = default
    ) => SendPaymentsAsync(payment, "fallback", cancellationToken);

    private async Task SendPaymentsAsync(
        PaymentApiRequest payment,
        string processor,
        CancellationToken cancellationToken = default
    )
    {
        PaymentResult result;
        if (processor == "default")
        {
            result = await defaultPaymentProcessor.PostAsync(payment, cancellationToken);
        }
        else if (processor == "fallback")
        {
            result = await fallbackPaymentProcessor.PostAsync(payment, cancellationToken);
        }
        else
        {
            throw new NotSupportedException(processor);
        }

        if (result == PaymentResult.Success)
        {
            await db.SavePaymentAsync(payment, processor);
            return;
        }

        if (result == PaymentResult.Duplicate)
        {
            logger.LogInformation(
                "Payment with correlation ID {CorrelationId} is a duplicate.",
                payment.CorrelationId
            );
            await db.SavePaymentAsync(payment, processor);

            return;
        }

        if (await db.StringGetAsync("best-processor") == processor)
            await db.StringSetAsync("best-processor", "none");
        var processorToRetry = result == PaymentResult.Timeout ? processor : "none";
        await db.SavePendingPaymentAsync(payment, processorToRetry);
        logger.LogWarning(
            "Default payment processor failed with {Error} for correlation ID {CorrelationId}",
            payment.CorrelationId,
            result.ToString().ToLowerInvariant()
        );
    }
}
