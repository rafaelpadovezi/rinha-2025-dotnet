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

var redis = ConnectionMultiplexer.Connect("localhost");
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

builder.Services.AddHostedService<ProcessorChecker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapOpenApi();
app.MapScalarApiReference();

app.UseHttpLogging();

app.MapGet(
    "/payments-summary",
    async ([FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to) =>
    {
        RedisKey[] keys =
        [
            "default-total-requests",
            "default-total-amount",
            "fallback-total-requests",
            "fallback-total-amount",
        ];
        var results = await db.StringGetAsync(keys);
        return Results.Ok(
            new PaymentSummaryResponse(
                new Summary((int)results[0], (double)results[1]),
                new Summary((int)results[2], (double)results[3])
            )
        );
    }
);

app.MapPost(
    "/payments",
    async ([FromBody] PaymentRequest request) =>
    {
        var correlationId = request.CorrelationId;
        var amount = request.Amount;

        var bestProcessor = await db.StringGetAsync("best-processor");
        if (bestProcessor == "default")
        {
            bool success = await defaultPaymentProcessor.PostAsync(
                new PaymentApiRequest(correlationId, amount, DateTimeOffset.UtcNow)
            );
            if (success)
            {
                await db.StringIncrementAsync("default-total-requests");
                await db.StringIncrementAsync("default-total-amount", amount);
                return Results.Ok();
            }

            app.Logger.LogInformation(
                "Default payment processor failed for correlation ID {CorrelationId}",
                correlationId
            );
        }

        if (bestProcessor == "fallback")
        {
            bool success = await fallbackPaymentProcessor.PostAsync(
                new PaymentApiRequest(correlationId, amount, DateTimeOffset.UtcNow)
            );
            if (success)
            {
                await db.StringIncrementAsync("fallback-total-requests");
                await db.StringIncrementAsync("fallback-total-amount", amount);
                return Results.Ok();
            }
            app.Logger.LogInformation(
                "Default payment processor failed for correlation ID {CorrelationId}",
                correlationId
            );
        }

        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
);

app.Run();
