using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.Indicators;

/// <summary>Projections of a candle series. Every indicator output is index-aligned to its input.</summary>
public static class CandleValues
{
    public static IReadOnlyList<decimal> Closes(IReadOnlyList<Candle> candles) => candles.Select(c => c.Close).ToList();
    public static IReadOnlyList<decimal> Highs(IReadOnlyList<Candle> candles) => candles.Select(c => c.High).ToList();
    public static IReadOnlyList<decimal> Lows(IReadOnlyList<Candle> candles) => candles.Select(c => c.Low).ToList();
}
