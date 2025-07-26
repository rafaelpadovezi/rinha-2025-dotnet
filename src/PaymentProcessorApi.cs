using System.Net;
using System.Text.Json;

namespace Rinha;

public class PaymentProcessorApi
{
    private readonly HttpClient _httpClient = new();
    private readonly HttpClient _healthCheckHttpClient = new();

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
                    SerializeOptions
                ) ?? UnhealthyResponse;
        }
        catch (Exception)
        {
            return UnhealthyResponse;
        }
    }

    public async Task<PaymentResult> PostAsync(
        PaymentApiRequest paymentApiRequest,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "payments",
                paymentApiRequest,
                cancellationToken
            );
            if (response.IsSuccessStatusCode)
            {
                return PaymentResult.Success;
            }

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                return PaymentResult.Duplicate;
            }

            if (response.StatusCode == HttpStatusCode.InternalServerError)
            {
                return PaymentResult.Failure;
            }

            return PaymentResult.Other;
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

    // public async Task<PaymentResult> PostIfNotExistAsync(PaymentApiRequest paymentApiRequest)
    // {
    //     try
    //     {
    //         var checkResponse = await _httpClient.GetAsync($"payments/{paymentApiRequest.CorrelationId}");
    //         if (checkResponse.IsSuccessStatusCode)
    //             return true;
    //         var response = await _httpClient.PostAsJsonAsync("payments", paymentApiRequest);
    //         return response.IsSuccessStatusCode;
    //     }
    //     catch (HttpRequestException)
    //     {
    //         return false;
    //     }
    //     catch (OperationCanceledException)
    //     {
    //         return false;
    //     }
    // }
}

public enum PaymentResult
{
    Success,
    Timeout,
    Failure,
    Duplicate,
    Other,
}
