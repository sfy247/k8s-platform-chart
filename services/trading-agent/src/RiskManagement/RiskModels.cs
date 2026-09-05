using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.RiskManagement;

/// <summary>
/// Why an order exists. This is not cosmetic: entries and exits are held to
/// different rules, because the failure modes are opposites. An entry that is
/// wrongly blocked costs an opportunity; an exit that is wrongly blocked
/// leaves a position open overnight, which is the one outcome a day-trading
/// system exists to prevent.
/// </summary>
public enum OrderIntent { Entry, Exit }

public sealed record RiskPolicy(
    decimal MaxPositionNotional,
    int MaxConcurrentPositions,
    decimal MaxDailyRealizedLoss,
    decimal MinimumCashReserve,
    decimal MaxPortfolioExposure,
    int MaxOrdersPerSymbolPerDay,
    int MaxTotalOrdersPerDay,
    TimeSpan MaxDataAge,
    bool RequirePaperMode,
    bool TradingEnabled,
    // ── Pattern day trader (FINRA) ───────────────────────────────────────
    // An account below the equity threshold may open and close the same
    // position at most MaxDayTradesUnderPdt times in a rolling five business
    // days. The broker enforces this; the agent models it so that the fourth
    // entry is refused here with an explanation rather than rejected at the
    // broker with an opaque error.
    decimal PdtEquityThreshold,
    int MaxDayTradesUnderPdt);

public sealed record AccountRiskState(
    decimal Cash,
    decimal PortfolioExposure,
    decimal DailyRealizedPnl,
    int OpenPositionCount,
    int TotalOrdersToday,
    int OrdersForSymbolToday,
    bool MarketOpen,
    bool IsPaperEndpoint,
    bool HasOpenOrderForSymbol,
    decimal ExistingPositionNotional,
    decimal Equity = 0m,
    int DayTradeCount = 0);

public sealed record RiskDecision(bool Approved, string Code, string Reason, ApprovedOrder? Order = null);

public sealed record ApprovedOrder(
    string ClientOrderId,
    string Symbol,
    TradeAction Action,
    decimal Notional,
    DateTimeOffset ApprovedAtUtc,
    OrderIntent Intent = OrderIntent.Entry);

// ─────────────────────────────────────────────────────────────────────────
// Day-trading session policy
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Where inside the session the agent is allowed to act.
///
/// The three windows exist for different reasons. The opening exclusion
/// avoids the auction's unstable spreads. The entry cutoff stops the agent
/// opening a position it will be forced to close minutes later, paying the
/// spread twice for no thesis. The flatten deadline is the hard one: past it
/// every position is closed regardless of P&L, because holding overnight is
/// a different strategy with different risk than the one being run.
/// </summary>
public sealed record SessionPolicy(
    TimeSpan NoEntryAfterOpen,
    TimeSpan NoEntryBeforeClose,
    TimeSpan FlattenBeforeClose)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (NoEntryAfterOpen < TimeSpan.Zero) errors.Add("session.skipFirstMinutesAfterOpen must not be negative.");
        if (NoEntryBeforeClose < TimeSpan.Zero) errors.Add("session.noNewEntriesMinutesBeforeClose must not be negative.");
        if (FlattenBeforeClose <= TimeSpan.Zero) errors.Add("session.flattenMinutesBeforeClose must be greater than zero.");

        // If entries were still allowed after the flatten deadline the agent
        // would buy and immediately liquidate, losing the spread every time.
        if (NoEntryBeforeClose < FlattenBeforeClose)
            errors.Add("session.noNewEntriesMinutesBeforeClose must be at least session.flattenMinutesBeforeClose.");

        return errors;
    }
}

/// <summary>Per-position invalidation, defined before the entry is taken.</summary>
public sealed record ExitPolicy(
    decimal StopLossPercent,
    decimal TakeProfitPercent,
    TimeSpan MaxHoldTime)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (StopLossPercent <= 0) errors.Add("exits.stopLossPercent must be greater than zero.");
        if (TakeProfitPercent <= 0) errors.Add("exits.takeProfitPercent must be greater than zero.");
        if (MaxHoldTime <= TimeSpan.Zero) errors.Add("exits.maxHoldMinutes must be greater than zero.");
        return errors;
    }
}

public enum ExitReason { None, SessionClose, StopLoss, TakeProfit, MaxHoldTime }

public sealed record ExitDecision(bool ShouldExit, ExitReason Reason, string Explanation)
{
    public static readonly ExitDecision Hold = new(false, ExitReason.None, "Within stop, target and session limits.");
}

/// <summary>
/// One open position as the broker reports it.
///
/// P&L comes from the broker rather than from a locally remembered entry
/// price. A restarted pod has no memory; the broker does, and reconciling
/// against it is the difference between a stop that survives a redeploy and
/// one that silently stops existing.
///
/// The P&L fields are nullable because an absent one must not be read as
/// zero. Zero would mean "flat", which is a live claim about the trade;
/// null means "the broker did not say", which suspends the stop and the
/// target while leaving the flatten deadline — the rule that does not need
/// P&L — in force.
/// </summary>
public sealed record PositionSnapshot(
    string Symbol,
    decimal Quantity,
    decimal MarketValue,
    decimal? AverageEntryPrice,
    decimal? CurrentPrice,
    decimal? UnrealizedPnl,
    decimal? UnrealizedPnlFraction,
    DateTimeOffset? OpenedAtUtc = null)
{
    public bool PnlKnown => UnrealizedPnlFraction is not null;
}
