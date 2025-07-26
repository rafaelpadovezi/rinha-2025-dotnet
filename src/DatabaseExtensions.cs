using System.Text.Json;
using StackExchange.Redis;

namespace Rinha;

public static class DatabaseExtensions
{
    public static async Task SavePaymentAsync(
        this IDatabase db,
        PaymentApiRequest paymentApiRequest,
        string processor
    )
    {
        var json = JsonSerializer.Serialize(
            new PaymentEvent(
                paymentApiRequest.CorrelationId,
                paymentApiRequest.Amount,
                paymentApiRequest.RequestedAt,
                processor
            ),
            AppJsonSerializerContext.Default.PaymentEvent
        );
        var timestamp = paymentApiRequest.RequestedAt.ToUnixTimeMilliseconds();
        var processorKey = $"{processor}Payments";
        await db.SortedSetAddAsync(processorKey, json, timestamp);
    }

    public static async Task SavePendingPaymentAsync(
        this IDatabase db,
        PaymentApiRequest paymentApiRequest,
        string processor
    )
    {
        var json = JsonSerializer.Serialize(
            new PaymentEvent(
                paymentApiRequest.CorrelationId,
                paymentApiRequest.Amount,
                paymentApiRequest.RequestedAt,
                processor
            ),
            AppJsonSerializerContext.Default.PaymentEvent
        );
        var processorKey = processor switch
        {
            "default" => "pendingDefaultPayments",
            "fallback" => "pendingFallbackPayments",
            _ => "pendingPayments",
        };
        await db.ListRightPushAsync(processorKey, json);
    }
}
