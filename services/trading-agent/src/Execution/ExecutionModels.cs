namespace ClaudeTradingAgent.Execution;

public sealed record BrokerOrderResult(
    string BrokerOrderId,
    string ClientOrderId,
    string Symbol,
    string Status,
    decimal? FilledQuantity,
    decimal? FilledAveragePrice,
    DateTimeOffset SubmittedAtUtc);

/// <summary>
/// The broker may or may not have accepted an order, and a follow-up lookup
/// could not settle which. rules/execution-rules.md: never blindly retry —
/// stop new entries and reconcile by client_order_id until the answer is known.
/// </summary>
public sealed class OrderStatusUncertainException(string clientOrderId, string symbol, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string ClientOrderId { get; } = clientOrderId;
    public string Symbol { get; } = symbol;
}
