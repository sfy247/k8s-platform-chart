namespace ClaudeTradingAgent.Portfolio;

public sealed record PositionSnapshot(string Symbol, decimal Quantity, decimal MarketValue, decimal UnrealizedPnl);

public sealed record PortfolioSnapshot(
    decimal Cash,
    decimal Equity,
    decimal BuyingPower,
    decimal RealizedPnlToday,
    IReadOnlyList<PositionSnapshot> Positions,
    DateTimeOffset TimestampUtc)
{
    public decimal GrossExposure => Positions.Sum(p => Math.Abs(p.MarketValue));
}
