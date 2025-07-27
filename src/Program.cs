using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Rinha;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateSlimBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();

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

app.Use(
    async (context, next) =>
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await next.Invoke();

        stopwatch.Stop();

        if (stopwatch.ElapsedMilliseconds >= 5)
        {
            var method = context.Request.Method;
            var path = context.Request.Path;
            var queryString = context.Request.QueryString.ToString();
            var fullPath = string.IsNullOrEmpty(queryString)
                ? path.ToString()
                : $"{path}{queryString}";

            app.Logger.LogInformation(
                "Request {Method} {Path} took {Duration}ms",
                method,
                fullPath,
                stopwatch.ElapsedMilliseconds
            );
        }
    }
);

// Configure the HTTP request pipeline.
app.MapOpenApi();
app.MapScalarApiReference();

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
    async ([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to) =>
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
