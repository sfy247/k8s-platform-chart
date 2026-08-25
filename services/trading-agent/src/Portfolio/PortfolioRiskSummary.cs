namespace ClaudeTradingAgent.Portfolio;

public sealed record PortfolioRiskSummary(decimal GrossExposure, decimal CashReserve, int PositionCount, bool ShouldStopTrading, string Reason);

public static class PortfolioRiskAnalyzer
{
    public static PortfolioRiskSummary Analyze(PortfolioSnapshot snapshot, decimal maxDailyLoss)
    {
        var stop = snapshot.RealizedPnlToday <= -Math.Abs(maxDailyLoss);
        return new PortfolioRiskSummary(
            snapshot.GrossExposure,
            snapshot.Cash,
            snapshot.Positions.Count,
            stop,
            stop ? "Daily realized loss limit reached." : "Portfolio remains within the supplied daily-loss condition.");
    }
}
