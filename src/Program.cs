using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;

using Polly;
using Polly.Extensions.Http;

using Rinha;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateSlimBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();

// Configure HttpClient with Polly retry policy
builder.Services.AddHttpClient("PaymentApiClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
    // .AddPolicyHandler(HttpPolicyExtensions
    //     .HandleTransientHttpError()
    //     .WaitAndRetryAsync(
    //         retryCount: 3,
    //         sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))/10));

builder.Logging.ClearProviders();
builder.Services.RemoveAll<IHttpMessageHandlerBuilderFilter>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
    options.SerializerOptions.DefaultBufferSize = 16 * 1024; // 16 KB
});

var redisConnectionString =
    builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Redis:ConnectionString is not configured.");
var redis = ConnectionMultiplexer.Connect(redisConnectionString);
var redisDb = redis.GetDatabase();
builder.Services.AddSingleton(redisDb);

var options = new UnboundedChannelOptions { SingleWriter = false, SingleReader = true };
var channel = Channel.CreateUnbounded<PaymentRequest>(options);
builder.Services.AddSingleton(channel);
builder.Services.AddHostedService<PaymentConsumerWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapOpenApi();
app.MapScalarApiReference();

app.MapPost(
    "/purge-payments",
    async (IDatabase db) =>
    {
        await db.KeyDeleteAsync("payments");

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
            var payment = JsonSerializer.Deserialize<PaymentEvent>(
                json!,
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
return;

static bool TryParsePaymentProcessor(ReadOnlySpan<char> jsonSpan, out Processor processor, out decimal amount)
{
    processor = default;
    amount = 0;

    // Procurar por "processor":"
    var processorIndex = jsonSpan.IndexOf("\"Processor\":");
    if (processorIndex == -1) return false;
    
    var processorStart = processorIndex + 12; // tamanho de "processor":""
    var processorEnd = processorStart + 1;
    
    var processorValue = jsonSpan.Slice(processorStart, 1);
    
    // Parse do processor
    if (processorValue is "0")
        processor = Processor.Default;
    else if (processorValue is "1")
        processor = Processor.Fallback;
    else
        return false;

    // Procurar por "amount":
    var amountIndex = jsonSpan.IndexOf("\"Amount\":");
    if (amountIndex == -1) return false;
    
    var amountStart = amountIndex + 9; // tamanho de "amount":
    var remaining = jsonSpan.Slice(amountStart);
    
    // Pular espaços em branco
    while (remaining.Length > 0 && char.IsWhiteSpace(remaining[0]))
    {
        remaining = remaining.Slice(1);
        amountStart++;
    }
    
    // Encontrar o fim do número (até vírgula ou fim do objeto)
    var amountEnd = 0;
    while (amountEnd < remaining.Length && 
           char.IsDigit(remaining[amountEnd]) || remaining[amountEnd] == '.')
    {
        amountEnd++;
    }
    
    if (amountEnd == 0) return false;
    
    var amountValue = jsonSpan.Slice(amountStart, amountEnd);
    return decimal.TryParse(amountValue, out amount);
}

[JsonSerializable(typeof(PaymentEvent))]
[JsonSerializable(typeof(PaymentRequest))]
[JsonSerializable(typeof(PaymentSummaryResponse))]
[JsonSerializable(typeof(Summary))]
[JsonSerializable(typeof(PaymentApiServiceHealthResponse))]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }
