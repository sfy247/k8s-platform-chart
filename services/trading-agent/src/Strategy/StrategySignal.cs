namespace ClaudeTradingAgent.Strategy;

public enum TradeAction { Hold, Buy, Sell }

/// <summary>
/// A trade proposal. The last three fields are required by v3's
/// trade-proposal skill: how the entry is placed, what proves the thesis
/// wrong, and what ends the trade. They default so existing strategies keep
/// compiling; the worker fills them from the exit policy before the proposal
/// reaches the risk engine and the audit.
/// </summary>
public sealed record StrategySignal(
    string Symbol,
    TradeAction Action,
    decimal ProposedNotional,
    decimal Confidence,
    string StrategyName,
    string ReasoningSummary,
    DateTimeOffset DataTimestampUtc,
    string EntryType = "market",
    string? Invalidation = null,
    string? Target = null);
