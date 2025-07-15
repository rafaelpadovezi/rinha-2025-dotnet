namespace Rinha;

public record PaymentRequest(Guid CorrelationId, decimal Amount);

public record PaymentSummaryResponse(Summary Default, Summary Fallback);

public record Summary(int TotalRequests, decimal TotalAmount);

public record PaymentApiRequest(Guid CorrelationId, decimal Amount, DateTimeOffset RequestedAt);

public record PaymentApiServiceHealthResponse(bool Failing, int MinResponseTime);

public record PaymentApiDetailsResponse(
    Guid CorrelationId,
    decimal Amount,
    DateTimeOffset RequestedAt
);

public record PaymentEvent(
    Guid CorrelationId,
    decimal Amount,
    DateTimeOffset RequestedAt,
    string Processor,
    string Result
);