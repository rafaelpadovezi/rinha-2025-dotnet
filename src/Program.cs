using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

using NpgsqlTypes;

using Rinha;

var builder = WebApplication.CreateSlimBuilder(args);

// Add services to the container.
// builder.Logging.ClearProviders();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.WebHost.ConfigureKestrel((_, serverOptions) =>
    {
        serverOptions.AddServerHeader = false;
    });
var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("Postgres"));
await using var dataSource = dataSourceBuilder.Build();
builder.Services.AddSingleton(dataSource);

var options =
    new UnboundedChannelOptions { SingleWriter = false, SingleReader = false, AllowSynchronousContinuations = true };
var channel = Channel.CreateUnbounded<PaymentRequest>(options);
builder.Services.AddSingleton(channel);
builder.Services.AddHostedService<PaymentConsumerWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapPost(
    "/payments",
    async ([FromBody] PaymentRequest request, Channel<PaymentRequest> queue) =>
    {
        request.RequestedAt = DateTimeOffset.UtcNow;

        await queue.Writer.WriteAsync(request);

        return Results.Ok();
    }
);

app.MapGet(
    "/payments-summary",
    async ([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, NpgsqlDataSource db) =>
    {
        var startScore = from?.ToUnixTimeMilliseconds() ?? double.NegativeInfinity;
        var endScore = to?.ToUnixTimeMilliseconds() ?? double.PositiveInfinity;
        var defaultPaymentsCount = 0;
        var fallbackPaymentsCount = 0;
        var defaultPaymentsAmount = 0m;
        var fallbackPaymentsAmount = 0m;

        // get from db the default and fallback payments summary
        await using (var conn = await db.OpenConnectionAsync())
        {
            // Default payments
            await using (var cmd = new NpgsqlCommand(
                "SELECT COUNT(*), SUM(amount) FROM payments WHERE processor = 0 AND requested_at >= @start AND requested_at <= @end",
                conn))
            {
                cmd.Parameters.AddWithValue("start", NpgsqlDbType.TimestampTz, from);
                cmd.Parameters.AddWithValue("end", NpgsqlDbType.TimestampTz, to);
                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        defaultPaymentsCount = reader.GetInt32(0);
                        defaultPaymentsAmount = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
                    }
                }
            }

            // Fallback payments
            await using (var cmd = new NpgsqlCommand(
                "SELECT COUNT(*), SUM(amount) FROM payments WHERE processor = 1 AND requested_at >= @start AND requested_at <= @end",
                conn))
            {
                cmd.Parameters.AddWithValue("start", from);
                cmd.Parameters.AddWithValue("end", to);
                await using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        fallbackPaymentsCount = reader.GetInt32(0);
                        fallbackPaymentsAmount = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
                    }
                }
            }
        }

        var defaultSummary = new Summary(defaultPaymentsCount, defaultPaymentsAmount);
        var fallbackSummary = new Summary(fallbackPaymentsCount, fallbackPaymentsAmount);
        var result = new PaymentSummaryResponse(defaultSummary, fallbackSummary);

        return Results.Ok(result);
    }
);

app.MapPost(
    "/purge-payments",
    async (NpgsqlDataSource db) =>
    {
        await using var conn = await db.OpenConnectionAsync();
        await using (var cmd = new NpgsqlCommand("TRUNCATE TABLE payments", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        return Results.Ok();
    }
);

app.Run();

[JsonSerializable(typeof(PaymentEvent))]
[JsonSerializable(typeof(PaymentRequest))]
[JsonSerializable(typeof(PaymentSummaryResponse))]
[JsonSerializable(typeof(Summary))]
[JsonSerializable(typeof(PaymentApiServiceHealthResponse))]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }