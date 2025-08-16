namespace Rinha;

public sealed record PaymentSummaryResponse(Summary Default, Summary Fallback);

public readonly struct Summary(int totalRequests, decimal totalAmount)
{
    public int TotalRequests { get; } = totalRequests;
    public decimal TotalAmount { get; } = totalAmount;
}

public sealed record PaymentRequest(Guid CorrelationId, decimal Amount)
{
    public DateTimeOffset RequestedAt { get; set; }
}

public sealed record PaymentApiServiceHealthResponse(bool Failing, int MinResponseTime);

public sealed record PaymentEvent(Guid CorrelationId, decimal Amount, Processor Processor);

public readonly struct PaymentDbEvent
{
    public decimal Amount { get; init; }
}

public enum Processor
{
    Default,
    Fallback,
}
