using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.Indicators;

/// <summary>
/// Volume-weighted average price, reset at the start of every trading session.
///
/// VWAP[i] = Σ(typical price × volume) / Σ volume over the session's candles
/// 0..i, where typical price = (high + low + close) / 3. Null while the
/// session has traded no volume. Carrying VWAP across sessions would make it
/// meaningless as the day's institutional reference price.
/// </summary>
public static class SessionVwap
{
    /// <param name="sessionOf">Session key for a candle's open time. Defaults to the New York exchange date.</param>
    public static IReadOnlyList<decimal?> Calculate(IReadOnlyList<Candle> candles, Func<DateTimeOffset, DateOnly>? sessionOf = null)
    {
        sessionOf ??= ExchangeSession.DateOf;
        var result = new decimal?[candles.Count];
        decimal pv = 0;
        long volume = 0;
        DateOnly? session = null;

        for (var i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            var day = sessionOf(c.TimestampUtc);
            if (day != session) { session = day; pv = 0; volume = 0; }

            pv += c.TypicalPrice * c.Volume;
            volume += c.Volume;
            result[i] = volume > 0 ? pv / volume : null;
        }
        return result;
    }
}
