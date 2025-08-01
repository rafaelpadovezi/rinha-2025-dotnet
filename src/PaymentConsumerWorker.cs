﻿using System.Threading.Channels;
using StackExchange.Redis;

namespace Rinha;

public class PaymentConsumerWorker : BackgroundService
{
    private readonly Channel<PaymentApiRequest> _queue;
    private readonly ILogger<PaymentConsumerWorker> _logger;
    private readonly PaymentProcessorApi _defaultPaymentProcessor;
    private readonly PaymentProcessorApi _fallbackPaymentProcessor;
    private readonly IDatabase _db;

    public PaymentConsumerWorker(
        IConfiguration configuration,
        IDatabase db,
        Channel<PaymentApiRequest> queue,
        ILogger<PaymentConsumerWorker> logger
    )
    {
        var defaultBaseUrl =
            configuration.GetValue<string>("PaymentProcessorDefault:BaseUrl")
            ?? throw new InvalidOperationException(
                "PaymentProcessorDefault:BaseUrl is not configured."
            );
        _defaultPaymentProcessor = new(defaultBaseUrl);
        var fallbackBaseUrl =
            configuration.GetValue<string>("PaymentProcessorFallback:BaseUrl")
            ?? throw new InvalidOperationException(
                "PaymentProcessorFallback:BaseUrl is not configured."
            );
        _fallbackPaymentProcessor = new(fallbackBaseUrl);

        _db = db;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (stoppingToken.IsCancellationRequested == false)
            {
                var payment = await _queue.Reader.ReadAsync(stoppingToken);
                await ProcessItemAsync(payment, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessItemAsync(PaymentApiRequest payment, CancellationToken stoppingToken)
    {
        var result = await _defaultPaymentProcessor.PostAsync(payment, stoppingToken);
        if (result == PaymentResult.Success)
        {
            await _db.SavePaymentAsync(payment, "default");
            return;
        }

        result = await _fallbackPaymentProcessor.PostAsync(payment, stoppingToken);
        if (result == PaymentResult.Success)
        {
            await _db.SavePaymentAsync(payment, "fallback");
            return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
        await _queue.Writer.WriteAsync(payment, stoppingToken);
    }
}
