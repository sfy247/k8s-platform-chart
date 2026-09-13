namespace ClaudeTradingAgent.MarketData;

/// <summary>The candle widths analysed in phase 1: 15m for the broad intraday trend, 5m for setup structure, 1m for timing.</summary>
public enum Timeframe
{
    OneMinute = 1,
    FiveMinutes = 5,
    FifteenMinutes = 15,
}

public static class TimeframeExtensions
{
    public static TimeSpan Duration(this Timeframe timeframe) => TimeSpan.FromMinutes((int)timeframe);

    public static string Label(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.OneMinute => "1m",
        Timeframe.FiveMinutes => "5m",
        Timeframe.FifteenMinutes => "15m",
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe)),
    };

    public static bool TryParse(string? value, out Timeframe timeframe)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "1m" or "1min": timeframe = Timeframe.OneMinute; return true;
            case "5m" or "5min": timeframe = Timeframe.FiveMinutes; return true;
            case "15m" or "15min": timeframe = Timeframe.FifteenMinutes; return true;
            default: timeframe = default; return false;
        }
    }
}

/// <summary>
/// Canonical OHLCV candle. <see cref="TimestampUtc"/> is the candle's OPEN
/// time, in UTC; the candle is complete — and may only be used — once
/// <see cref="CloseTimeUtc"/> has passed.
/// </summary>
public sealed record Candle(
    string Symbol,
    Timeframe Timeframe,
    DateTimeOffset TimestampUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume)
{
    public DateTimeOffset CloseTimeUtc => TimestampUtc + Timeframe.Duration();

    /// <summary>(high + low + close) / 3, the price VWAP weights by volume.</summary>
    public decimal TypicalPrice => (High + Low + Close) / 3m;

    public static Candle FromBar(string symbol, Bar bar, Timeframe timeframe = Timeframe.OneMinute) =>
        new(symbol.ToUpperInvariant(), timeframe, bar.TimestampUtc.ToUniversalTime(), bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
}

public static class CandleSeries
{
    /// <summary>
    /// Rejects a series that analysis could silently mis-read: mixed symbols
    /// or widths, out-of-order or duplicate times, or impossible prices.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<Candle> candles)
    {
        var errors = new List<string>();
        for (var i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            if (c.Low > Math.Min(c.Open, c.Close) || c.High < Math.Max(c.Open, c.Close) || c.Low <= 0)
                errors.Add($"Candle {i} at {c.TimestampUtc:o} has impossible prices.");
            if (c.Volume < 0) errors.Add($"Candle {i} at {c.TimestampUtc:o} has negative volume.");
            if (i == 0) continue;
            var prev = candles[i - 1];
            if (c.Symbol != prev.Symbol || c.Timeframe != prev.Timeframe) errors.Add($"Candle {i} mixes symbol or timeframe.");
            if (c.TimestampUtc <= prev.TimestampUtc) errors.Add($"Candle {i} at {c.TimestampUtc:o} is not after the previous candle.");
        }
        return errors;
    }
}

/// <summary>
/// The US equity trading session a moment belongs to, by New York date.
/// Uses the IANA timezone database (present in the .NET runtime images and
/// on the lab host), so daylight-saving changes are handled by the OS data,
/// not by hand-written rules.
/// </summary>
public static class ExchangeSession
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static DateOnly DateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, NewYork).DateTime);

    public static TimeOnly TimeOfDay(DateTimeOffset instant) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, NewYork).DateTime);
}

/// <summary>
/// Builds 5m and 15m candles from 1m candles.
///
/// Buckets are aligned to clock minutes (09:30, 09:35, 09:45 ...), which on
/// US equities is also aligned to the open. A bucket is emitted only once it
/// has CLOSED at the evaluation time: a half-formed 5m candle at 10:03 would
/// present an incomplete high, low and close as if they were final, which is
/// look-ahead in disguise.
/// </summary>
public static class CandleAggregator
{
    public static IReadOnlyList<Candle> Aggregate(IReadOnlyList<Candle> oneMinute, Timeframe target, DateTimeOffset asOfUtc)
    {
        if (target == Timeframe.OneMinute)
            return oneMinute.Where(c => c.CloseTimeUtc <= asOfUtc).ToList();
        if (oneMinute.Any(c => c.Timeframe != Timeframe.OneMinute))
            throw new ArgumentException("Aggregation expects 1-minute candles.", nameof(oneMinute));

        var width = (int)target;
        var result = new List<Candle>();
        foreach (var group in oneMinute
                     .Where(c => c.CloseTimeUtc <= asOfUtc)
                     .GroupBy(c => BucketStart(c.TimestampUtc, width))
                     .OrderBy(g => g.Key))
        {
            var bucket = group.OrderBy(c => c.TimestampUtc).ToList();
            var start = group.Key;
            if (start + target.Duration() > asOfUtc) continue;   // still forming

            result.Add(new Candle(
                bucket[0].Symbol, target, start,
                bucket[0].Open, bucket.Max(c => c.High), bucket.Min(c => c.Low), bucket[^1].Close,
                bucket.Sum(c => c.Volume)));
        }
        return result;
    }

    private static DateTimeOffset BucketStart(DateTimeOffset utc, int widthMinutes)
    {
        var t = utc.ToUniversalTime();
        var floored = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero);
        return floored.AddMinutes(t.Minute - t.Minute % widthMinutes);
    }
}
