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
        _httpClient.Timeout = TimeSpan.FromMilliseconds(20);
        _healthCheckHttpClient.BaseAddress = new(baseUrl);
        _healthCheckHttpClient.Timeout = TimeSpan.FromSeconds(6);
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

    public async Task<(bool Success, bool isTimeout)> PostAsync(PaymentApiRequest paymentApiRequest)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("payments", paymentApiRequest);
            var success = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.UnprocessableEntity;
            return (success, false);
        }
        catch (HttpRequestException)
        {
            return (false, false);
        }
        catch (OperationCanceledException)
        {
            return (false, true);
        }
    }

    public async Task<bool> PostIfNotExistAsync(PaymentApiRequest paymentApiRequest)
    {
        try
        {
            var checkResponse = await _httpClient.GetAsync($"payments/{paymentApiRequest.CorrelationId}");
            if (checkResponse.IsSuccessStatusCode)
                return true;
            var response = await _httpClient.PostAsJsonAsync("payments", paymentApiRequest);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.UnprocessableEntity;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}