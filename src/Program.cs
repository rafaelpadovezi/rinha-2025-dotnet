using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using Rinha;
using StackExchange.Redis;

var builder = WebApplication.CreateSlimBuilder(args);

// Add services to the container.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

var redisConnectionString =
    builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Redis:ConnectionString is not configured.");
var redis = ConnectionMultiplexer.Connect(redisConnectionString);
var redisDb = redis.GetDatabase();
builder.Services.AddSingleton(redisDb);

var options = new UnboundedChannelOptions { SingleWriter = false, SingleReader = false };
var channel = Channel.CreateUnbounded<PaymentRequest>(options);
builder.Services.AddSingleton(channel);
builder.Services.AddHostedService<PaymentConsumerWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.

app.MapPost(
    "/purge-payments",
    async () =>
    {
        await redisDb.KeyDeleteAsync("payments");

        return Results.Ok();
    }
);

app.MapGet(
    "/payments-summary",
    async ([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, IDatabase db) =>
    {
        var startScore = from?.ToUnixTimeMilliseconds() ?? double.NegativeInfinity;
        var endScore = to?.ToUnixTimeMilliseconds() ?? double.PositiveInfinity;
        var paymentsJson = await db.SortedSetRangeByScoreAsync("payments", startScore, endScore);
        var defaultPaymentsCount = 0;
        var fallbackPaymentsCount = 0;
        var defaultPaymentsAmount = 0m;
        var fallbackPaymentsAmount = 0m;

        foreach (var json in paymentsJson)
        {
            // Deserialize from bytes to avoid intermediate string allocations
            var bytes = (byte[])json!;
            var payment = JsonSerializer.Deserialize(
                bytes.AsSpan(),
                AppJsonSerializerContext.Default.PaymentEvent
            );
            if (payment.Processor == Processor.Default)
            {
                defaultPaymentsCount++;
                defaultPaymentsAmount += payment.Amount;
            }
            else if (payment.Processor == Processor.Fallback)
            {
                fallbackPaymentsCount++;
                fallbackPaymentsAmount += payment.Amount;
            }
        }
        var defaultSummary = new Summary(defaultPaymentsCount, defaultPaymentsAmount);
        var fallbackSummary = new Summary(fallbackPaymentsCount, fallbackPaymentsAmount);
        var result = new PaymentSummaryResponse(defaultSummary, fallbackSummary);

        return Results.Ok(result);
    }
);

app.MapPost(
    "/payments",
    async ([FromBody] PaymentRequest request, Channel<PaymentRequest> queue) =>
    {
        request.RequestedAt = DateTimeOffset.UtcNow;

        await queue.Writer.WriteAsync(request);

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
