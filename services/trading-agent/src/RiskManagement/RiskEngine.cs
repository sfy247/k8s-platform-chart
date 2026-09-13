using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.RiskManagement;

/// <summary>
/// The only thing that can approve an order.
///
/// Implements the fifteen required checks of rules/risk-management-rules.md.
/// Entries and exits both pass through here, but limits that stop the agent
/// TAKING ON risk apply to entries only — an agent that has hit its daily
/// loss limit must still be able to close the position that caused it.
/// </summary>
public sealed class RiskEngine
{
    public RiskDecision Evaluate(
        StrategySignal proposal,
        AccountRiskState state,
        RiskPolicy policy,
        IReadOnlySet<string> allowlist,
        DateTimeOffset now,
        OrderIntent intent = OrderIntent.Entry)
    {
        var buying = proposal.Action == TradeAction.Buy;

        // ── Every order, entry or exit ────────────────────────────────────
        if (policy.RequirePaperMode && !state.IsPaperEndpoint) return Reject("NOT_PAPER", "Execution endpoint is not paper trading.");       // 1
        if (!policy.TradingEnabled) return Reject("KILL_SWITCH", "Trading is disabled.");                                                     // 2
        if (!state.MarketOpen) return Reject("MARKET_CLOSED", "Market is closed.");                                                           // 3
        if (proposal.Action == TradeAction.Hold) return Reject("NO_TRADE", "Strategy returned HOLD.");
        if (!allowlist.Contains(proposal.Symbol)) return Reject("SYMBOL_NOT_ALLOWED", "Symbol is not allowlisted.");                          // 5
        if (proposal.ProposedNotional <= 0) return Reject("POSITION_LIMIT", "Proposed notional must be greater than zero.");
        if (state.HasOpenOrderForSymbol) return Reject("DUPLICATE_EXPOSURE", "An open order already exists for this symbol.");               // 14
        if (proposal.Action == TradeAction.Sell && state.ExistingPositionNotional <= 0) return Reject("NO_LONG_POSITION", "Sell would create a short position.");        // 15
        if (proposal.Action == TradeAction.Sell && proposal.ProposedNotional > state.ExistingPositionNotional) return Reject("SHORTING_BLOCKED", "Sell exceeds the owned long position.");

        if (intent == OrderIntent.Exit) return Approve(proposal, now, intent);

        // ── Entries only ──────────────────────────────────────────────────
        if (state.SessionState != SessionState.EntryWindow)                                                                                    // 4
            return Reject("OUTSIDE_ENTRY_WINDOW", $"Session is {SessionWindow.Label(state.SessionState)}; only ENTRY_WINDOW permits new entries.");
        if (now - proposal.DataTimestampUtc > policy.MaxDataAge) return Reject("STALE_DATA", "Proposal is based on stale market data.");     // 6
        if (state.OrderStateUncertain)
            return Reject("ORDER_STATE_UNCERTAIN", "An earlier order's broker status is unconfirmed; new entries stop until it is reconciled.");
        if (state.DailyLossLockout || state.DailyRealizedPnl <= -policy.MaxDailyLoss)                                                         // 12
            return Reject("DAILY_LOSS_LIMIT", "Daily loss limit reached; new entries are blocked for the rest of the session.");
        if (proposal.ProposedNotional > policy.MaxNotionalPerTrade) return Reject("POSITION_LIMIT", "Proposed notional exceeds the per-trade limit.");

        if (buying)
        {
            if (state.ExistingPositionNotional > 0)
                return Reject("PYRAMIDING_BLOCKED", "Already long this symbol; adding to a position is not enabled.");
            if (state.OpenPositionCount >= policy.MaxConcurrentPositions) return Reject("POSITION_COUNT_LIMIT", "Maximum concurrent positions reached.");      // 9
            if (state.PortfolioExposure + proposal.ProposedNotional > policy.MaxTotalExposure) return Reject("EXPOSURE_LIMIT", "Total exposure limit would be exceeded.");   // 10

            // 11: cash against the strategy's own capital, not the broker's
            // simulated balance. Losses today shrink it; unbanked gains do not
            // grow it.
            var strategyCash = policy.StrategyCapital + Math.Min(state.DailyRealizedPnl, 0m) - state.PortfolioExposure;
            if (proposal.ProposedNotional > strategyCash)
                return Reject("STRATEGY_CAPITAL", $"Only {strategyCash:C2} of the {policy.StrategyCapital:C0} strategy capital is available.");
            if (proposal.ProposedNotional > state.Cash) return Reject("INSUFFICIENT_CASH", "Broker cash does not cover the order.");

            var estimatedLoss = proposal.ProposedNotional * policy.StopLossPercent / 100m;                                                 // 13
            if (estimatedLoss > policy.MaxEstimatedLossPerTrade)
                return Reject("PER_TRADE_LOSS_LIMIT", $"Estimated loss {estimatedLoss:C2} at the stop exceeds the {policy.MaxEstimatedLossPerTrade:C2} per-trade limit.");
        }

        if (state.TotalOrdersToday >= policy.MaxTotalOrdersPerDay) return Reject("ORDER_RATE_LIMIT", "Daily order limit reached.");
        if (state.OrdersForSymbolToday >= policy.MaxOrdersPerSymbolPerDay) return Reject("SYMBOL_ORDER_LIMIT", "Per-symbol daily order limit reached.");
        if (PatternDayTraderRejection(state, policy) is { } pdt) return pdt;

        return Approve(proposal, now, intent);
    }

    private static RiskDecision Approve(StrategySignal proposal, DateTimeOffset now, OrderIntent intent)
    {
        var id = $"cta-{now:yyyyMMddHHmmssfff}-{proposal.Symbol}-{Guid.NewGuid():N}";
        if (id.Length > 64) id = id[..64];
        return new RiskDecision(true, "APPROVED", "All deterministic risk checks passed.",
            new ApprovedOrder(id, proposal.Symbol, proposal.Action, proposal.ProposedNotional, now, intent));
    }

    /// <summary>
    /// FINRA pattern-day-trader limit. Above the equity threshold it does not
    /// apply and the count is never read; below it, an absent count fails
    /// closed. Entries only — never a reason to keep a position overnight.
    /// </summary>
    private static RiskDecision? PatternDayTraderRejection(AccountRiskState state, RiskPolicy policy)
    {
        if (policy.MaxDayTradesUnderPdt <= 0) return null;
        if (state.Equity <= 0) return Reject("EQUITY_UNKNOWN", "Account equity is unavailable; the pattern-day-trader limit cannot be evaluated.");
        if (state.Equity >= policy.PdtEquityThreshold) return null;
        if (state.DayTradeCount is not { } used)
            return Reject("PDT_COUNT_UNKNOWN", $"Equity is under {policy.PdtEquityThreshold:C0} but the broker did not report a day-trade count.");
        return used >= policy.MaxDayTradesUnderPdt
            ? Reject("PDT_LIMIT", $"{used} day trades used against a limit of {policy.MaxDayTradesUnderPdt}.")
            : null;
    }

    private static RiskDecision Reject(string code, string reason) => new(false, code, reason);
}
