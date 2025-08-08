namespace Rinha;

public record PaymentSummaryResponse(Summary Default, Summary Fallback);

public struct Summary(int totalRequests, decimal totalAmount)
{
    public int TotalRequests { get; set; } = totalRequests;
    public decimal TotalAmount { get; set; } = totalAmount;
}

public record PaymentRequest(Guid CorrelationId, decimal Amount)
{
    public DateTimeOffset RequestedAt { get; set; }
}

public record PaymentApiServiceHealthResponse(bool Failing, int MinResponseTime);

public record PaymentEvent(
    Guid CorrelationId,
    decimal Amount,
    DateTimeOffset RequestedAt,
    Processor Processor
);

public enum Processor
{
    Default,
    Fallback,
}
