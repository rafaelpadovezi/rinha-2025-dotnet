using System.Net;
using System.Text.Json;
using Polly;
using Polly.Extensions.Http;

namespace Rinha;

public class PaymentProcessorApi
{
    private readonly HttpClient _httpClient = new();
    private readonly HttpClient _healthCheckHttpClient = new();

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.NotFound)
            .WaitAndRetryAsync(
                3,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) / 10
            );

    public PaymentProcessorApi(string baseUrl)
    {
        _httpClient.BaseAddress = new(baseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _healthCheckHttpClient.BaseAddress = new(baseUrl);
        _healthCheckHttpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    private static readonly PaymentApiServiceHealthResponse UnhealthyResponse = new(true, 0);

    public async Task<PaymentApiServiceHealthResponse> GetHealthAsync()
    {
        try
        {
            var response = await _healthCheckHttpClient.GetAsync("payments/service-health");
            if (!response.IsSuccessStatusCode)
            {
                return UnhealthyResponse;
            }

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<PaymentApiServiceHealthResponse>(
                    content,
                    AppJsonSerializerContext.Default.PaymentApiServiceHealthResponse
                ) ?? UnhealthyResponse;
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
        var retryPolicy = GetRetryPolicy();
        try
        {
            var response = await retryPolicy.ExecuteAsync(() =>
                _httpClient.PostAsJsonAsync(
                    "payments",
                    paymentApiRequest,
                    AppJsonSerializerContext.Default.PaymentRequest,
                    cancellationToken
                )
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