namespace ClaudeTradingAgent.Indicators;

/// <summary>
/// Simple and exponential moving averages.
///
/// Output is aligned to input: element i uses only values 0..i, and is null
/// until the period has enough data. Appending later values never changes an
/// earlier output — the property that keeps these free of look-ahead.
/// </summary>
public static class MovingAverage
{
    public static IReadOnlyList<decimal?> Sma(IReadOnlyList<decimal> values, int period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        var result = new decimal?[values.Count];
        decimal window = 0;
        for (var i = 0; i < values.Count; i++)
        {
            window += values[i];
            if (i >= period) window -= values[i - period];
            if (i >= period - 1) result[i] = window / period;
        }
        return result;
    }

    /// <summary>
    /// EMA with α = 2 / (period + 1), seeded with the SMA of the first
    /// <paramref name="period"/> values. Null before the seed exists.
    /// </summary>
    public static IReadOnlyList<decimal?> Ema(IReadOnlyList<decimal> values, int period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        var result = new decimal?[values.Count];
        if (values.Count < period) return result;

        var alpha = 2m / (period + 1);
        decimal ema = 0;
        for (var i = 0; i < period; i++) ema += values[i];
        ema /= period;
        result[period - 1] = ema;

        for (var i = period; i < values.Count; i++)
        {
            ema = (values[i] - ema) * alpha + ema;
            result[i] = ema;
        }
        return result;
    }
}
