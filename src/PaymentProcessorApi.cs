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
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.Timeout = TimeSpan.FromMilliseconds(1400);
        _healthCheckHttpClient.BaseAddress = new Uri(baseUrl);
        _healthCheckHttpClient.Timeout = TimeSpan.FromSeconds(5);
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
        catch (Exception e)
        {
            return UnhealthyResponse;
        }
    }

    public async Task<bool> PostAsync(PaymentApiRequest paymentApiRequest)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("payments", paymentApiRequest);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false; // If the request fails, we assume the payment processor is down
        }
        catch (OperationCanceledException)
        {
            return false; // If the request is canceled, we assume the payment processor is down
        }
    }
}
