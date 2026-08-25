using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.RiskManagement;

public sealed class RiskEngine
{
    public RiskDecision Evaluate(
        StrategySignal proposal,
        AccountRiskState state,
        RiskPolicy policy,
        IReadOnlySet<string> allowlist,
        DateTimeOffset now)
    {
        if (!policy.TradingEnabled) return Reject("KILL_SWITCH", "Trading is disabled.");
        if (policy.RequirePaperMode && !state.IsPaperEndpoint) return Reject("NOT_PAPER", "Execution endpoint is not paper trading.");
        if (!state.MarketOpen) return Reject("MARKET_CLOSED", "Market is closed.");
        if (proposal.Action == TradeAction.Hold) return Reject("NO_TRADE", "Strategy returned HOLD.");
        if (!allowlist.Contains(proposal.Symbol)) return Reject("SYMBOL_NOT_ALLOWED", "Symbol is not allowlisted.");
        if (now - proposal.DataTimestampUtc > policy.MaxDataAge) return Reject("STALE_DATA", "Proposal is based on stale market data.");
        if (proposal.ProposedNotional <= 0 || proposal.ProposedNotional > policy.MaxPositionNotional) return Reject("POSITION_LIMIT", "Proposed notional exceeds the per-position limit.");
        if (state.DailyRealizedPnl <= -policy.MaxDailyRealizedLoss) return Reject("DAILY_LOSS_LIMIT", "Daily realized loss limit reached.");
        if (state.OpenPositionCount >= policy.MaxConcurrentPositions && proposal.Action == TradeAction.Buy) return Reject("POSITION_COUNT_LIMIT", "Maximum concurrent positions reached.");
        if (state.PortfolioExposure + proposal.ProposedNotional > policy.MaxPortfolioExposure && proposal.Action == TradeAction.Buy) return Reject("EXPOSURE_LIMIT", "Portfolio exposure limit would be exceeded.");
        if (state.Cash - proposal.ProposedNotional < policy.MinimumCashReserve && proposal.Action == TradeAction.Buy) return Reject("CASH_RESERVE", "Minimum cash reserve would be violated.");
        if (state.TotalOrdersToday >= policy.MaxTotalOrdersPerDay) return Reject("ORDER_RATE_LIMIT", "Daily order limit reached.");
        if (state.OrdersForSymbolToday >= policy.MaxOrdersPerSymbolPerDay) return Reject("SYMBOL_ORDER_LIMIT", "Per-symbol daily order limit reached.");
        if (state.HasOpenOrderForSymbol) return Reject("DUPLICATE_EXPOSURE", "An open order already exists for this symbol.");
        if (proposal.Action == TradeAction.Sell && state.ExistingPositionNotional <= 0) return Reject("NO_LONG_POSITION", "Sell would create a short position.");
        if (proposal.Action == TradeAction.Sell && proposal.ProposedNotional > state.ExistingPositionNotional) return Reject("SHORTING_BLOCKED", "Sell notional exceeds the existing long position.");

        var id = $"cta-{now:yyyyMMddHHmmssfff}-{proposal.Symbol}-{Guid.NewGuid():N}";
        if (id.Length > 64) id = id[..64];
        var approved = new ApprovedOrder(id, proposal.Symbol, proposal.Action, proposal.ProposedNotional, now);
        return new RiskDecision(true, "APPROVED", "All deterministic risk checks passed.", approved);
    }

    private static RiskDecision Reject(string code, string reason) => new(false, code, reason);
}
