using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.RiskManagement;

/// <summary>
/// The only thing that can approve an order.
///
/// Entries and exits both pass through here — nothing routes around it — but
/// they are judged differently. The limits that exist to stop the agent
/// taking on risk (exposure, cash, order rate, daily loss, PDT) are entry
/// rules. Applying them to exits would mean an agent that has hit its daily
/// loss limit can no longer close the position that caused it, which turns a
/// risk control into a trap.
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
        // ── Rules that hold for every order, entry or exit ────────────────
        if (!policy.TradingEnabled) return Reject("KILL_SWITCH", "Trading is disabled.");
        if (policy.RequirePaperMode && !state.IsPaperEndpoint) return Reject("NOT_PAPER", "Execution endpoint is not paper trading.");
        if (!state.MarketOpen) return Reject("MARKET_CLOSED", "Market is closed.");
        if (proposal.Action == TradeAction.Hold) return Reject("NO_TRADE", "Strategy returned HOLD.");
        if (!allowlist.Contains(proposal.Symbol)) return Reject("SYMBOL_NOT_ALLOWED", "Symbol is not allowlisted.");
        if (proposal.ProposedNotional <= 0) return Reject("POSITION_LIMIT", "Proposed notional must be greater than zero.");

        // An order already working for this symbol. Applies to exits too: a
        // second liquidation while the first is unfilled would oversell into
        // a short position.
        if (state.HasOpenOrderForSymbol) return Reject("DUPLICATE_EXPOSURE", "An open order already exists for this symbol.");

        if (proposal.Action == TradeAction.Sell && state.ExistingPositionNotional <= 0) return Reject("NO_LONG_POSITION", "Sell would create a short position.");
        if (proposal.Action == TradeAction.Sell && proposal.ProposedNotional > state.ExistingPositionNotional) return Reject("SHORTING_BLOCKED", "Sell notional exceeds the existing long position.");

        // ── Entry-only rules ─────────────────────────────────────────────
        if (intent == OrderIntent.Entry)
        {
            if (now - proposal.DataTimestampUtc > policy.MaxDataAge) return Reject("STALE_DATA", "Proposal is based on stale market data.");
            if (proposal.ProposedNotional > policy.MaxPositionNotional) return Reject("POSITION_LIMIT", "Proposed notional exceeds the per-position limit.");
            if (state.DailyRealizedPnl <= -policy.MaxDailyRealizedLoss) return Reject("DAILY_LOSS_LIMIT", "Daily loss limit reached.");
            if (state.OpenPositionCount >= policy.MaxConcurrentPositions && proposal.Action == TradeAction.Buy) return Reject("POSITION_COUNT_LIMIT", "Maximum concurrent positions reached.");
            if (state.PortfolioExposure + proposal.ProposedNotional > policy.MaxPortfolioExposure && proposal.Action == TradeAction.Buy) return Reject("EXPOSURE_LIMIT", "Portfolio exposure limit would be exceeded.");
            if (state.Cash - proposal.ProposedNotional < policy.MinimumCashReserve && proposal.Action == TradeAction.Buy) return Reject("CASH_RESERVE", "Minimum cash reserve would be violated.");
            if (state.TotalOrdersToday >= policy.MaxTotalOrdersPerDay) return Reject("ORDER_RATE_LIMIT", "Daily order limit reached.");
            if (state.OrdersForSymbolToday >= policy.MaxOrdersPerSymbolPerDay) return Reject("SYMBOL_ORDER_LIMIT", "Per-symbol daily order limit reached.");

            if (PatternDayTraderRejection(state, policy) is { } pdt) return pdt;
        }

        var id = $"cta-{now:yyyyMMddHHmmssfff}-{proposal.Symbol}-{Guid.NewGuid():N}";
        if (id.Length > 64) id = id[..64];
        var approved = new ApprovedOrder(id, proposal.Symbol, proposal.Action, proposal.ProposedNotional, now, intent);
        return new RiskDecision(true, "APPROVED", "All deterministic risk checks passed.", approved);
    }

    /// <summary>
    /// FINRA's pattern-day-trader rule: under the equity threshold, an account
    /// gets a small number of same-day round trips per rolling five business
    /// days. The broker is the enforcer — this exists so the agent stops one
    /// trade early with a reason an operator can read, instead of discovering
    /// the limit as a broker rejection after it has already committed.
    ///
    /// Entries only. Refusing to close a position because the account is near
    /// its day-trade count would leave it open overnight to avoid a
    /// bookkeeping limit, which is a far worse trade than the one being
    /// avoided.
    /// </summary>
    private static RiskDecision? PatternDayTraderRejection(AccountRiskState state, RiskPolicy policy)
    {
        if (policy.MaxDayTradesUnderPdt <= 0) return null;

        if (state.Equity <= 0)
            return Reject("EQUITY_UNKNOWN", "Account equity is unavailable; the pattern-day-trader limit cannot be evaluated.");

        if (state.Equity >= policy.PdtEquityThreshold) return null;

        if (state.DayTradeCount >= policy.MaxDayTradesUnderPdt)
            return Reject("PDT_LIMIT",
                $"{state.DayTradeCount} day trades used against a limit of {policy.MaxDayTradesUnderPdt} for accounts under {policy.PdtEquityThreshold:C0}.");

        return null;
    }

    private static RiskDecision Reject(string code, string reason) => new(false, code, reason);
}
