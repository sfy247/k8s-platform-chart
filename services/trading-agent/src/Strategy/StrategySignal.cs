namespace ClaudeTradingAgent.Strategy;

public enum TradeAction { Hold, Buy, Sell }

public sealed record StrategySignal(
    string Symbol,
    TradeAction Action,
    decimal ProposedNotional,
    decimal Confidence,
    string StrategyName,
    string ReasoningSummary,
    DateTimeOffset DataTimestampUtc);
