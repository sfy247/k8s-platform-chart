using ClaudeTradingAgent.Execution;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.Persistence;

/// <summary>
/// One row per evaluation: what the strategy proposed, what the risk engine
/// decided, and what the broker did.
///
/// Every evaluation is recorded, including the ones where market data was
/// unusable. A decision not to trade is as auditable as a decision to trade,
/// and "why did nothing happen on Tuesday" is a question logs alone cannot
/// answer once they have aged out.
/// </summary>
public sealed record DecisionRecord
{
    public required DateTimeOffset DecidedAtUtc { get; init; }
    public required string Symbol { get; init; }

    // Strategy — null when evaluation stopped before a proposal existed,
    // which happens when market data was rejected as unusable.
    public string? StrategyName { get; init; }
    public TradeAction? Action { get; init; }
    public decimal? ProposedNotional { get; init; }
    public decimal? Confidence { get; init; }
    public string? ReasoningSummary { get; init; }
    public DateTimeOffset? DataTimestampUtc { get; init; }

    // Risk — always present. Code is the machine-readable outcome:
    // APPROVED, KILL_SWITCH, NO_DATA, STALE_DATA, POSITION_LIMIT, ...
    public required bool Approved { get; init; }
    public required string DecisionCode { get; init; }
    public required string DecisionReason { get; init; }
    public string? ClientOrderId { get; init; }

    // Execution — only when an order actually reached the broker.
    public string? BrokerOrderId { get; init; }
    public string? BrokerStatus { get; init; }
    public decimal? FilledQuantity { get; init; }
    public decimal? FilledAveragePrice { get; init; }

    // Context, so a row can be interpreted without knowing what the
    // configuration was at the time.
    public required bool TradingEnabled { get; init; }
    public required bool MarketOpen { get; init; }
    public required string Pod { get; init; }

    /// <summary>An evaluation that never produced a proposal.</summary>
    public static DecisionRecord NoData(
        string symbol, string reason, bool tradingEnabled, bool marketOpen, string pod) => new()
    {
        DecidedAtUtc = DateTimeOffset.UtcNow,
        Symbol = symbol,
        Approved = false,
        DecisionCode = "NO_DATA",
        DecisionReason = reason,
        TradingEnabled = tradingEnabled,
        MarketOpen = marketOpen,
        Pod = pod,
    };

    /// <summary>A completed evaluation, with or without an order.</summary>
    public static DecisionRecord From(
        StrategySignal proposal,
        RiskDecision decision,
        BrokerOrderResult? broker,
        bool tradingEnabled,
        bool marketOpen,
        string pod) => new()
    {
        DecidedAtUtc = DateTimeOffset.UtcNow,
        Symbol = proposal.Symbol,
        StrategyName = proposal.StrategyName,
        Action = proposal.Action,
        ProposedNotional = proposal.ProposedNotional,
        Confidence = proposal.Confidence,
        ReasoningSummary = proposal.ReasoningSummary,
        DataTimestampUtc = proposal.DataTimestampUtc,
        Approved = decision.Approved,
        DecisionCode = decision.Code,
        DecisionReason = decision.Reason,
        ClientOrderId = decision.Order?.ClientOrderId,
        BrokerOrderId = broker?.BrokerOrderId,
        BrokerStatus = broker?.Status,
        FilledQuantity = broker?.FilledQuantity,
        FilledAveragePrice = broker?.FilledAveragePrice,
        TradingEnabled = tradingEnabled,
        MarketOpen = marketOpen,
        Pod = pod,
    };
}
