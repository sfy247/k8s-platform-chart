using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.Indicators;

/// <summary>
/// Relative volume (RVOL): how unusual a candle's volume is. Two definitions,
/// named explicitly because "relative volume" means different things in
/// different tools.
/// </summary>
public static class RelativeVolume
{
    /// <summary>
    /// <b>Trailing RVOL.</b> RVOL[i] = volume[i] ÷ mean(volume[i − lookback .. i − 1]).
    /// The current candle is excluded from its own baseline. Null for the first
    /// <paramref name="lookback"/> candles, or when the baseline is zero.
    /// Good for "is this candle busier than the ones just before it".
    /// </summary>
    public static IReadOnlyList<decimal?> Trailing(IReadOnlyList<Candle> candles, int lookback)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lookback, 1);
        var result = new decimal?[candles.Count];
        decimal window = 0;
        for (var i = 0; i < candles.Count; i++)
        {
            if (i >= lookback)
            {
                var baseline = window / lookback;
                result[i] = baseline > 0 ? candles[i].Volume / baseline : null;
                window -= candles[i - lookback].Volume;
            }
            window += candles[i].Volume;
        }
        return result;
    }

    /// <summary>
    /// <b>Time-of-day RVOL.</b> RVOL[i] = volume[i] ÷ the mean volume of the
    /// candles that opened at the SAME New York clock time in up to
    /// <paramref name="lookbackSessions"/> earlier sessions.
    /// Only earlier sessions count; sessions with no candle at that time are
    /// skipped. Null when no earlier session has one, or the baseline is zero.
    /// Intraday volume is U-shaped — the open is always busy — so this is the
    /// definition that says whether today's open is unusual for an open.
    /// </summary>
    public static IReadOnlyList<decimal?> ByTimeOfDay(
        IReadOnlyList<Candle> candles,
        int lookbackSessions,
        Func<DateTimeOffset, DateOnly>? sessionOf = null,
        Func<DateTimeOffset, TimeOnly>? timeOf = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lookbackSessions, 1);
        sessionOf ??= ExchangeSession.DateOf;
        timeOf ??= ExchangeSession.TimeOfDay;

        var result = new decimal?[candles.Count];
        // For each clock time: (session, volume) from sessions already seen, oldest first.
        var history = new Dictionary<TimeOnly, List<(DateOnly Session, long Volume)>>();

        for (var i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            var session = sessionOf(c.TimestampUtc);
            var time = timeOf(c.TimestampUtc);
            if (!history.TryGetValue(time, out var seen)) history[time] = seen = [];

            var earlier = seen.Where(s => s.Session < session).TakeLast(lookbackSessions).ToList();
            if (earlier.Count > 0)
            {
                var baseline = (decimal)earlier.Sum(s => s.Volume) / earlier.Count;
                result[i] = baseline > 0 ? c.Volume / baseline : null;
            }
            seen.Add((session, c.Volume));
        }
        return result;
    }
}
