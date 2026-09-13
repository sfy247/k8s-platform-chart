using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.RiskManagement;

/// <summary>
/// Why an order exists. Entries and exits are held to different rules
/// because their failure modes are opposites: a wrongly blocked entry costs
/// an opportunity, a wrongly blocked exit leaves a position open overnight.
/// </summary>
public enum OrderIntent { Entry, Exit }

/// <summary>
/// Where the trading day is, as the v3 session-management skill defines it.
/// Only <see cref="EntryWindow"/> permits new entries.
/// </summary>
public enum SessionState
{
    MarketClosed,
    PreMarketDisabled,
    EntryWindow,
    ManagementOnly,
    FlattenWindow,
}

public sealed record RiskPolicy
{
    public required bool TradingEnabled { get; init; }
    public required bool RequirePaperMode { get; init; }

    /// <summary>
    /// The capital the strategy behaves as if it manages. The paper broker
    /// shows ~$100,000; the experiment is sized at $100, and every cash and
    /// exposure check is made against this figure, not the broker's balance.
    /// </summary>
    public required decimal StrategyCapital { get; init; }

    public required decimal MaxNotionalPerTrade { get; init; }
    public required int MaxConcurrentPositions { get; init; }
    public required decimal MaxTotalExposure { get; init; }
    public required decimal MaxDailyLoss { get; init; }

    /// <summary>Notional × stop distance may not exceed this.</summary>
    public required decimal MaxEstimatedLossPerTrade { get; init; }
    public required decimal StopLossPercent { get; init; }

    public required TimeSpan MaxDataAge { get; init; }
    public required int MaxOrdersPerSymbolPerDay { get; init; }
    public required int MaxTotalOrdersPerDay { get; init; }

    // Pattern day trader (FINRA). Zero disables the check.
    public decimal PdtEquityThreshold { get; init; }
    public int MaxDayTradesUnderPdt { get; init; }
}

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
    // Null when the broker did not report it — never zero as a stand-in.
    int? DayTradeCount = null,
    // Defaults fail closed: a caller that forgets to say where the session
    // is gets no entries, not unrestricted ones.
    SessionState SessionState = SessionState.MarketClosed,
    bool DailyLossLockout = false,
    bool OrderStateUncertain = false);

public sealed record RiskDecision(bool Approved, string Code, string Reason, ApprovedOrder? Order = null);

public sealed record ApprovedOrder(
    string ClientOrderId,
    string Symbol,
    TradeAction Action,
    decimal Notional,
    DateTimeOffset ApprovedAtUtc,
    OrderIntent Intent = OrderIntent.Entry);

/// <summary>
/// Session windows as New York clock times, from the v3 config.
///
/// On a normal 09:30–16:00 day each boundary lands exactly on its clock time.
/// On a shortened session each keeps its distance from the real open or
/// close: a 15:55 flatten on a 13:00 half-day becomes 12:55. A fixed 15:55
/// would come three hours after the market closed — i.e. a held position.
/// </summary>
public sealed record SessionPolicy(TimeOnly EntryStartEt, TimeOnly EntryCutoffEt, TimeOnly FlattenEt)
{
    public static readonly TimeOnly RegularOpenEt = new(9, 30);
    public static readonly TimeOnly RegularCloseEt = new(16, 0);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (EntryStartEt < RegularOpenEt) errors.Add("entryStartTimeEt must be at or after 09:30.");
        if (EntryCutoffEt <= EntryStartEt) errors.Add("entryCutoffTimeEt must be after entryStartTimeEt.");
        if (FlattenEt <= EntryCutoffEt) errors.Add("flattenTimeEt must be after entryCutoffTimeEt, or the agent buys what it must immediately sell.");
        if (FlattenEt >= RegularCloseEt) errors.Add("flattenTimeEt must be before 16:00.");
        return errors;
    }
}

/// <summary>Per-position invalidation, defined before the entry is taken.</summary>
public sealed record ExitPolicy(decimal StopLossPercent, decimal TakeProfitPercent, TimeSpan MaxHoldTime)
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
/// One open position as the broker reports it. Only symbol and market value
/// are required; an absent P&L suspends the stop rather than failing the
/// cycle, because a failed cycle also skips the flatten.
/// </summary>
public sealed record PositionSnapshot(
    string Symbol,
    decimal? Quantity,
    decimal MarketValue,
    decimal? AverageEntryPrice,
    decimal? CurrentPrice,
    decimal? UnrealizedPnl,
    decimal? UnrealizedPnlFraction,
    DateTimeOffset? OpenedAtUtc = null)
{
    public bool PnlKnown => UnrealizedPnlFraction is not null;
}
