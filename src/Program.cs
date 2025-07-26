using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;

using Rinha;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();

builder.Services.AddHttpLogging(logging =>
{
    logging.CombineLogs = true;
    logging.RequestBodyLogLimit = 4096;
    logging.ResponseBodyLogLimit = 4096;
    logging.LoggingFields =
        HttpLoggingFields.Duration
        | HttpLoggingFields.RequestMethod
        | HttpLoggingFields.RequestPath
        | HttpLoggingFields.RequestQuery
        | HttpLoggingFields.ResponseStatusCode
        | HttpLoggingFields.RequestBody
        | HttpLoggingFields.ResponseBody;
});

var redisConnectionString =
    builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Redis:ConnectionString is not configured.");
var redis = ConnectionMultiplexer.Connect(redisConnectionString);
var db = redis.GetDatabase();
builder.Services.AddSingleton(db);

// var defaultBaseUrl =
//     builder.Configuration.GetValue<string>("PaymentProcessorDefault:BaseUrl")
//     ?? throw new InvalidOperationException("PaymentProcessorDefault:BaseUrl is not configured.");
// var defaultPaymentProcessor = new PaymentProcessorApi(defaultBaseUrl);
// builder.Services.AddKeyedSingleton("default", defaultPaymentProcessor);
// var fallbackBaseUrl =
//     builder.Configuration.GetValue<string>("PaymentProcessorFallback:BaseUrl")
//     ?? throw new InvalidOperationException("PaymentProcessorFallback:BaseUrl is not configured.");
// var fallbackPaymentProcessor = new PaymentProcessorApi(fallbackBaseUrl);
// builder.Services.AddKeyedSingleton("fallback", fallbackPaymentProcessor);
// builder.Services.AddScoped<PaymentService>();

var options = new UnboundedChannelOptions { SingleWriter = false, SingleReader = false };
var queue = Channel.CreateUnbounded<PaymentApiRequest>(options);
builder.Services.AddSingleton(queue);
builder.Services.AddHostedService<PaymentConsumerWorker>();

var requestTimeout = builder.Configuration.GetValue<int?>("RequestTimeout") ?? 1000;

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapOpenApi();
app.MapScalarApiReference();

// app.UseHttpLogging();

app.MapPost("/purge-payments", async () =>
{
    await db.KeyDeleteAsync("fallbackPayments");
    await db.KeyDeleteAsync("defaultPayments");
    await db.KeyDeleteAsync("pendingDefaultPayments");
    await db.KeyDeleteAsync("pendingFallbackPayments");
    await db.KeyDeleteAsync("pendingPayments");

    return Results.Ok();
});

app.MapGet(
    "/payments-summary",
    async ([FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to) =>
    {
        // Log the amount of time it took to execute this endpoint
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        stopwatch.Start();
        var startScore = from.ToUnixTimeMilliseconds();
        var endScore = to.ToUnixTimeMilliseconds();
        var batch = db.CreateBatch();
        var defaultPaymentsTask = batch.SortedSetRangeByScoreAsync(
            "defaultPayments",
            startScore,
            endScore
        );
        var fallbackPaymentsTask = batch.SortedSetRangeByScoreAsync(
            "fallbackPayments",
            startScore,
            endScore
        );
        batch.Execute();

        var defaultPaymentsJson = await defaultPaymentsTask;
        var fallbackPaymentsJson = await fallbackPaymentsTask;
        var defaultPayments = defaultPaymentsJson
            .Select(json => JsonSerializer.Deserialize<PaymentEvent>(
                json!,
                AppJsonSerializerContext.Default.PaymentEvent))
            .ToArray();
        var defaultSummary = new Summary(
            defaultPayments.Length,
            defaultPayments.Sum(p => p.Amount)
        );
        var fallbackPayments = fallbackPaymentsJson
            .Select(json => JsonSerializer.Deserialize<PaymentEvent>(
                json!,
                AppJsonSerializerContext.Default.PaymentEvent))
            .ToArray();
        var fallbackSummary = new Summary(
            fallbackPayments.Length,
            fallbackPayments.Sum(p => p.Amount)
        );
        var result = new PaymentSummaryResponse(defaultSummary, fallbackSummary);
        stopwatch.Stop();
        app.Logger.LogInformation(
            "Payments {from:O} {to:O} summary endpoint executed in {ElapsedMilliseconds} ms. Results: {@result}",
            from,
            to,
            stopwatch.ElapsedMilliseconds,
            result
        );

        return Results.Ok(result);
    }
);

app.MapPost(
    "/payments",
    async ([FromBody] PaymentRequest request) =>
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var payment = new PaymentApiRequest(
            request.CorrelationId,
            request.Amount,
            DateTimeOffset.UtcNow
        );

        await queue.Writer.WriteAsync(payment);

        stopwatch.Stop();
        if (stopwatch.ElapsedMilliseconds > 50)
            app.Logger.LogInformation(
                "Payment request with correlation ID {CorrelationId} processed in {ElapsedMilliseconds} ms",
                request.CorrelationId,
                stopwatch.ElapsedMilliseconds
            );
        return Results.Ok();
    }
);

app.Run();

[JsonSerializable(typeof(PaymentEvent))]
[JsonSerializable(typeof(PaymentApiRequest))]
[JsonSerializable(typeof(PaymentRequest))]
[JsonSerializable(typeof(PaymentSummaryResponse))]
[JsonSerializable(typeof(Summary))]
internal partial class AppJsonSerializerContext : JsonSerializerContext {}