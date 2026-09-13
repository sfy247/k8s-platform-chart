using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.Indicators;

/// <summary>True range and Wilder's average true range.</summary>
public static class AverageTrueRange
{
    /// <summary>
    /// max(high − low, |high − previous close|, |low − previous close|).
    /// The first candle has no previous close, so its true range is high − low.
    /// </summary>
    public static IReadOnlyList<decimal> TrueRange(IReadOnlyList<Candle> candles)
    {
        var result = new decimal[candles.Count];
        for (var i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            var range = c.High - c.Low;
            if (i > 0)
            {
                var prevClose = candles[i - 1].Close;
                range = Math.Max(range, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose)));
            }
            result[i] = range;
        }
        return result;
    }

    /// <summary>
    /// Wilder's ATR: the first value (at index period − 1) is the mean of the
    /// first <paramref name="period"/> true ranges; after that
    /// ATR[i] = (ATR[i−1] × (period − 1) + TR[i]) / period. Null before the first value.
    /// </summary>
    public static IReadOnlyList<decimal?> Calculate(IReadOnlyList<Candle> candles, int period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        var tr = TrueRange(candles);
        var result = new decimal?[candles.Count];
        if (candles.Count < period) return result;

        decimal atr = 0;
        for (var i = 0; i < period; i++) atr += tr[i];
        atr /= period;
        result[period - 1] = atr;

        for (var i = period; i < candles.Count; i++)
        {
            atr = (atr * (period - 1) + tr[i]) / period;
            result[i] = atr;
        }
        return result;
    }
}
