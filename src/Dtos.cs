namespace Rinha;

public record PaymentRequest(Guid CorrelationId, double Amount);

public record PaymentSummaryResponse(Summary Default, Summary Fallback);

public record Summary(int TotalRequests, double TotalAmount);

public record PaymentApiRequest(Guid CorrelationId, double Amount, DateTimeOffset RequestedAt);

public record PaymentApiServiceHealthResponse(bool Failing, int MinResponseTime);

public record PaymentApiDetailsResponse(
    Guid CorrelationId,
    double Amount,
    DateTimeOffset RequestedAt
);
