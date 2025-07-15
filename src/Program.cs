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

if (builder.Configuration.GetValue<bool?>("RunBackgroundService") ?? false)
{
    builder.Services.AddHostedService<ProcessorChecker>();
    builder.Services.AddHostedService<PendingPaymentsHandler>();
}

var app = builder.Build();

await db.KeyDeleteAsync("payments");
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
        var results = await db.SortedSetRangeByScoreAsync("payments", startScore, endScore);
        var payments = results.Select(json => JsonSerializer.Deserialize<PaymentEvent>(json!)).ToList();
        var defaultSummary = payments
            .Where(p => p.Processor == "default")
            .GroupBy(p => p.Processor)
            .Select(g => new Summary(g.Count(), g.Sum(p => p.Amount)))
            .FirstOrDefault() ?? new Summary(0, 0);
        var fallbackSummary = payments
            .Where(p => p.Processor == "fallback")
            .GroupBy(p => p.Processor)
            .Select(g => new Summary(g.Count(), g.Sum(p => p.Amount)))
            .FirstOrDefault() ?? new Summary(0, 0);
        return Results.Ok(
            new PaymentSummaryResponse(
                defaultSummary,
                fallbackSummary
            )
        );
    }
);

app.MapPost(
    "/payments",
    async ([FromBody] PaymentRequest request) =>
    {
        var payment = new PaymentApiRequest(request.CorrelationId, request.Amount, DateTimeOffset.UtcNow);

        var bestProcessor = await db.StringGetAsync("best-processor");
        if (bestProcessor == "default")
        {
            var (success, isTimeout) = await defaultPaymentProcessor.PostAsync(payment);
            if (success || isTimeout)
            {
                await SavePaymentAsync(payment, "default");
                return Results.Ok("Payment processed by default processor.");
            }
            await db.StringSetAsync("best-processor", "none");
            await SavePendingPaymentAsync(payment, "default");

            app.Logger.LogWarning(
                "Default payment processor failed for correlation ID {CorrelationId}",
                payment.CorrelationId
            );
        }

        if (bestProcessor == "fallback")
        {
            var (success, isTimeout) = await fallbackPaymentProcessor.PostAsync(payment);
            if (success || isTimeout)
            {
                await SavePaymentAsync(payment, "fallback");
                return Results.Ok("Payment processed by fallback processor.");
            }
            await db.StringSetAsync("best-processor", "none");
            var procesor = isTimeout ? "none" : "fallback";
            await SavePendingPaymentAsync(payment, procesor);
            
            app.Logger.LogWarning(
                "Default payment processor failed for correlation ID {CorrelationId}",
                payment.CorrelationId
            );
        }

        return Results.InternalServerError();
        // await SavePendingPaymentAsync(payment, "none");
        //
        // return Results.Ok("Payment is pending, will be processed later.");
    }
);

app.Run();
return;

async Task SavePaymentAsync(PaymentApiRequest paymentApiRequest, string processor)
{
    var json = JsonSerializer.Serialize(new PaymentEvent(
        paymentApiRequest.CorrelationId, paymentApiRequest.Amount, paymentApiRequest.RequestedAt, processor, "success"
    ));
    var timestamp = paymentApiRequest.RequestedAt.ToUnixTimeSeconds();
    await db.SortedSetAddAsync("payments", json, timestamp);
}

async Task SavePendingPaymentAsync(PaymentApiRequest paymentApiRequest, string processor)
{
    var json = JsonSerializer.Serialize(new PaymentEvent(
        paymentApiRequest.CorrelationId, paymentApiRequest.Amount, paymentApiRequest.RequestedAt, processor, "pending"
    ));
    await db.ListRightPushAsync("pendingPayments", json);
}