using System.Text.Json;
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

var defaultBaseUrl =
    builder.Configuration.GetValue<string>("PaymentProcessorDefault:BaseUrl")
    ?? throw new InvalidOperationException("PaymentProcessorDefault:BaseUrl is not configured.");
var defaultPaymentProcessor = new PaymentProcessorApi(defaultBaseUrl);
builder.Services.AddKeyedSingleton("default", defaultPaymentProcessor);
var fallbackBaseUrl =
    builder.Configuration.GetValue<string>("PaymentProcessorFallback:BaseUrl")
    ?? throw new InvalidOperationException("PaymentProcessorFallback:BaseUrl is not configured.");
var fallbackPaymentProcessor = new PaymentProcessorApi(fallbackBaseUrl);
builder.Services.AddKeyedSingleton("fallback", fallbackPaymentProcessor);
builder.Services.AddScoped<PaymentService>();

if (builder.Configuration.GetValue<bool?>("RunBackgroundService") ?? false)
{
    builder.Services.AddHostedService<ProcessorChecker>();
    builder.Services.AddHostedService<PendingPaymentsHandler>();
    builder.Services.AddHostedService<PendingDefaultPaymentsHandler>();
    builder.Services.AddHostedService<PendingFallbackPaymentsHandler>();
}

var app = builder.Build();

await db.KeyDeleteAsync("fallbackPayments");
await db.KeyDeleteAsync("defaultPayments");
await db.KeyDeleteAsync("pendingDefaultPayments");
await db.KeyDeleteAsync("pendingFallbackPayments");
await db.KeyDeleteAsync("pendingPayments");

// Configure the HTTP request pipeline.
app.MapOpenApi();
app.MapScalarApiReference();

// app.UseHttpLogging();

app.MapGet(
    "/payments-summary",
    async ([FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to) =>
    {
        var startScore = from.ToUnixTimeSeconds();
        var endScore = to.ToUnixTimeSeconds();
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
            .Select(json => JsonSerializer.Deserialize<PaymentEvent>(json!))
            .ToArray();
        var defaultSummary = new Summary(
            defaultPayments.Length,
            defaultPayments.Sum(p => p.Amount)
        );
        var fallbackPayments = fallbackPaymentsJson
            .Select(json => JsonSerializer.Deserialize<PaymentEvent>(json!))
            .ToArray();
        var fallbackSummary = new Summary(
            fallbackPayments.Length,
            fallbackPayments.Sum(p => p.Amount)
        );

        return Results.Ok(new PaymentSummaryResponse(defaultSummary, fallbackSummary));
    }
);

app.MapPost(
    "/payments",
    async ([FromBody] PaymentRequest request, PaymentService paymentService) =>
    {
        var payment = new PaymentApiRequest(
            request.CorrelationId,
            request.Amount,
            DateTimeOffset.UtcNow
        );

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var bestProcessor = await db.StringGetAsync("best-processor");
        if (bestProcessor == "default")
        {
            await paymentService.SendDefaultPaymentsAsync(payment, cts.Token);
            return Results.Ok();
        }

        if (bestProcessor == "fallback")
        {
            await paymentService.SendFallbackPaymentsAsync(payment, cts.Token);
            return Results.Ok();
        }

        // await db.SavePendingPaymentAsync(payment, "none");

        return Results.InternalServerError();
    }
);

app.Run();
