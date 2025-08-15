using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;

namespace Rinha;

public class PaymentProcessorApi
{
    private readonly HttpClient _httpClient;
    private readonly HttpClient _healthCheckHttpClient = new();

    public PaymentProcessorApi(string baseUrl)
    {
        _healthCheckHttpClient.BaseAddress = new(baseUrl);
        _healthCheckHttpClient.Timeout = TimeSpan.FromSeconds(10);

        var socketHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            AllowAutoRedirect = false,
        };
        var retryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(
                new HttpRetryStrategyOptions
                {
                    BackoffType = DelayBackoffType.Exponential,
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromMilliseconds(250),
                    UseJitter = false,
                }
            )
            .Build();
        var resilienceHandler = new ResilienceHandler(retryPipeline)
        {
            InnerHandler = socketHandler,
        };
        _httpClient = new(resilienceHandler);
        _httpClient.BaseAddress = new(baseUrl + "/payments");
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    private static readonly PaymentApiServiceHealthResponse UnhealthyResponse = new(true, 0);

    public async Task<PaymentApiServiceHealthResponse> GetHealthAsync()
    {
        try
        {
            using var response = await _healthCheckHttpClient.GetAsync("payments/service-health");
            if (!response.IsSuccessStatusCode)
            {
                return UnhealthyResponse;
            }

            var health = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.PaymentApiServiceHealthResponse
            );
            return health ?? UnhealthyResponse;
        }
        catch (Exception)
        {
            return UnhealthyResponse;
        }
    }

    public async Task<PaymentResult> PostAsync(
        PaymentRequest paymentApiRequest,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            string? path = null;
            using var response = await _httpClient.PostAsJsonAsync(
                path,
                paymentApiRequest,
                AppJsonSerializerContext.Default.PaymentRequest,
                cancellationToken
            );
            if (response.IsSuccessStatusCode)
            {
                return PaymentResult.Success;
            }

            return response.StatusCode switch
            {
                HttpStatusCode.UnprocessableEntity => PaymentResult.Duplicate,
                HttpStatusCode.InternalServerError => PaymentResult.Failure,
                _ => PaymentResult.Other,
            };
        }
        catch (HttpRequestException)
        {
            return PaymentResult.Other;
        }
        catch (OperationCanceledException)
        {
            return PaymentResult.Timeout;
        }
    }
}

public enum PaymentResult
{
    Success,
    Timeout,
    Failure,
    Duplicate,
    Other,
}