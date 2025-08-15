using System.Text.Json;
using StackExchange.Redis;

namespace Rinha;

public static class DatabaseExtensions
{
    private const string SortedSetKey = "payments";

    public static Task SavePaymentAsync(
        this IDatabase db,
        PaymentRequest paymentApiRequest,
        Processor processor
    )
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            new(paymentApiRequest.CorrelationId, paymentApiRequest.Amount, processor),
            AppJsonSerializerContext.Default.PaymentEvent
        );
        var timestamp = paymentApiRequest.RequestedAt.ToUnixTimeMilliseconds();
        return db.SortedSetAddAsync(SortedSetKey, jsonBytes, timestamp);
    }
}
