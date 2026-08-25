namespace ClaudeTradingAgent.Execution;

public sealed record BrokerOrderResult(
    string BrokerOrderId,
    string ClientOrderId,
    string Symbol,
    string Status,
    decimal? FilledQuantity,
    decimal? FilledAveragePrice,
    DateTimeOffset SubmittedAtUtc);
