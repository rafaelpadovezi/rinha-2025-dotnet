using System.Net;
using System.Text.Json.Serialization.Metadata;
using Polly;
using Polly.Extensions.Http;
using Polly.Retry;

namespace Rinha;

public class PaymentProcessorApi
{
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        AllowAutoRedirect = false,
    };
    private readonly HttpClient _httpClient = new(SharedHandler);
    private readonly HttpClient _healthCheckHttpClient = new();

    private static readonly AsyncRetryPolicy<HttpResponseMessage> RetryPolicy = HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg => msg.StatusCode == HttpStatusCode.NotFound)
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) / 10);

    public PaymentProcessorApi(string baseUrl)
    {
        _httpClient.BaseAddress = new(baseUrl + "/payments");
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _healthCheckHttpClient.BaseAddress = new(baseUrl);
        _healthCheckHttpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    private static readonly PaymentApiServiceHealthResponse UnhealthyResponse = new(true, 0);

    public async Task<PaymentApiServiceHealthResponse> GetHealthAsync()
    {
        try
        {
            using var response = await _healthCheckHttpClient.GetAsync(
                "payments/service-health",
                HttpCompletionOption.ResponseHeadersRead
            );
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
            // Use a static lambda with Polly Context to avoid capturing outer variables.
            static async Task<HttpResponseMessage> PostWithContextAsync(
                Context ctx,
                CancellationToken ct
            )
            {
                var client = (HttpClient)ctx["client"]!;
                var payment = (PaymentRequest)ctx["payment"]!;
                var typeInfo = (JsonTypeInfo<PaymentRequest>)ctx["typeInfo"]!;

                using var request = new HttpRequestMessage();
                request.Method = HttpMethod.Post;
                request.Content = JsonContent.Create(payment, typeInfo);
                return await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct
                );
            }

            var ctx = new Context
            {
                ["client"] = _httpClient,
                ["payment"] = paymentApiRequest,
                ["typeInfo"] = AppJsonSerializerContext.Default.PaymentRequest,
            };

            var response = await RetryPolicy.ExecuteAsync(
                PostWithContextAsync,
                ctx,
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
