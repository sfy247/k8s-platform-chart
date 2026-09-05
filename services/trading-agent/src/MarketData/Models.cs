namespace ClaudeTradingAgent.MarketData;

public sealed record QuoteSnapshot(string Symbol, decimal Bid, decimal Ask, DateTimeOffset TimestampUtc)
{
    public decimal Mid => (Bid + Ask) / 2m;
    public decimal SpreadBps => Mid <= 0 ? decimal.MaxValue : ((Ask - Bid) / Mid) * 10_000m;
}

public sealed record Bar(decimal Open, decimal High, decimal Low, decimal Close, long Volume, DateTimeOffset TimestampUtc);
public sealed record AssetMetadata(string Symbol, bool Tradable, bool Fractionable, string AssetClass);

/// <summary>
/// The exchange clock. <see cref="ExchangeOffset"/> is the venue's UTC offset
/// at this instant, taken from the broker rather than from a local timezone
/// database: the container image is not guaranteed to ship tzdata, and a
/// day-trading system that guesses when the session ends is a system that
/// holds positions overnight by accident.
/// </summary>
public sealed record MarketClock(
    bool IsOpen,
    DateTimeOffset TimestampUtc,
    DateTimeOffset NextOpenUtc,
    DateTimeOffset NextCloseUtc,
    TimeSpan ExchangeOffset);

/// <summary>
/// Today's regular trading session, from the exchange calendar.
///
/// Both bounds come from the broker's calendar, so early closes — the day
/// after Thanksgiving, Christmas Eve — are handled as facts rather than as
/// an assumption that every session ends at 16:00.
/// </summary>
public sealed record TradingSession(DateOnly Date, DateTimeOffset OpenUtc, DateTimeOffset CloseUtc)
{
    public TimeSpan Elapsed(DateTimeOffset now) => now - OpenUtc;
    public TimeSpan Remaining(DateTimeOffset now) => CloseUtc - now;
    public TimeSpan Length => CloseUtc - OpenUtc;
}
