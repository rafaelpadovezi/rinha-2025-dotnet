using Npgsql;

using NpgsqlTypes;

namespace Rinha;

public static class DatabaseExtensions
{
    public static async Task SavePaymentAsync(
        this NpgsqlDataSource db,
        PaymentRequest paymentApiRequest,
        Processor processor
    )
    {
        await using var command = db.CreateCommand(
            "INSERT INTO payments (correlation_id, amount, requested_at, processor) VALUES (@correlation_id, @amount, @requested_at, @processor)");
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Uuid, paymentApiRequest.CorrelationId);
        command.Parameters.AddWithValue("amount", NpgsqlDbType.Numeric, paymentApiRequest.Amount);
        command.Parameters.AddWithValue("requested_at", NpgsqlDbType.TimestampTz, paymentApiRequest.RequestedAt);
        command.Parameters.AddWithValue("processor", NpgsqlDbType.Integer, (int)processor);

        await command.ExecuteNonQueryAsync();
    }
}
