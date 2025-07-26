using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using Rinha;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateSlimBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();

// builder.Services.AddHttpLogging(logging =>
// {
//     logging.CombineLogs = true;
//     logging.RequestBodyLogLimit = 4096;
//     logging.ResponseBodyLogLimit = 4096;
//     logging.LoggingFields =
//         HttpLoggingFields.Duration
//         | HttpLoggingFields.RequestMethod
//         | HttpLoggingFields.RequestPath
//         | HttpLoggingFields.RequestQuery
//         | HttpLoggingFields.ResponseStatusCode
//         | HttpLoggingFields.RequestBody
//         | HttpLoggingFields.ResponseBody;
// });

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

var redisConnectionString =
    builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Redis:ConnectionString is not configured.");
var redis = ConnectionMultiplexer.Connect(redisConnectionString);
var db = redis.GetDatabase();
builder.Services.AddSingleton(db);

var options = new UnboundedChannelOptions { SingleWriter = false, SingleReader = false };
var queue = Channel.CreateUnbounded<PaymentApiRequest>(options);
builder.Services.AddSingleton(queue);
builder.Services.AddHostedService<PaymentConsumerWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapOpenApi();
app.MapScalarApiReference();

// app.UseHttpLogging();

app.MapPost(
    "/purge-payments",
    async () =>
    {
        await db.KeyDeleteAsync("payments");

        return Results.Ok();
    }
);

app.MapGet(
    "/payments-summary",
    async ([FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to) =>
    {
        var startScore = from.ToUnixTimeMilliseconds();
        var endScore = to.ToUnixTimeMilliseconds();
        var paymentsJson = await db.SortedSetRangeByScoreAsync("payments", startScore, endScore);
        var defaultPaymentsCount = 0;
        var fallbackPaymentsCount = 0;
        var defaultPaymentsAmount = 0m;
        var fallbackPaymentsAmount = 0m;

        foreach (var json in paymentsJson)
        {
            var payment = JsonSerializer.Deserialize<PaymentEvent>(
                json!,
                AppJsonSerializerContext.Default.PaymentEvent
            );
            if (payment.Processor == "default")
            {
                defaultPaymentsCount++;
                defaultPaymentsAmount += payment.Amount;
            }
            else if (payment.Processor == "fallback")
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
    async ([FromBody] PaymentRequest request) =>
    {
        var payment = new PaymentApiRequest(
            request.CorrelationId,
            request.Amount,
            DateTimeOffset.UtcNow
        );

        await queue.Writer.WriteAsync(payment);

        return Results.Ok();
    }
);

app.Run();

[JsonSerializable(typeof(PaymentEvent))]
[JsonSerializable(typeof(PaymentApiRequest))]
[JsonSerializable(typeof(PaymentRequest))]
[JsonSerializable(typeof(PaymentSummaryResponse))]
[JsonSerializable(typeof(Summary))]
[JsonSerializable(typeof(PaymentApiServiceHealthResponse))]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }
