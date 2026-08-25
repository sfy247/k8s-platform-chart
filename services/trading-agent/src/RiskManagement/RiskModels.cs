using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.RiskManagement;

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
    bool TradingEnabled);

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
    decimal ExistingPositionNotional);

public sealed record RiskDecision(bool Approved, string Code, string Reason, ApprovedOrder? Order = null);

public sealed record ApprovedOrder(
    string ClientOrderId,
    string Symbol,
    TradeAction Action,
    decimal Notional,
    DateTimeOffset ApprovedAtUtc);
