namespace ClaudeTradingAgent.MarketData;

public sealed record QuoteSnapshot(string Symbol, decimal Bid, decimal Ask, DateTimeOffset TimestampUtc)
{
    public decimal Mid => (Bid + Ask) / 2m;
    public decimal SpreadBps => Mid <= 0 ? decimal.MaxValue : ((Ask - Bid) / Mid) * 10_000m;
}

public sealed record Bar(decimal Open, decimal High, decimal Low, decimal Close, long Volume, DateTimeOffset TimestampUtc);
public sealed record AssetMetadata(string Symbol, bool Tradable, bool Fractionable, string AssetClass);
public sealed record MarketClock(bool IsOpen, DateTimeOffset TimestampUtc, DateTimeOffset NextOpenUtc, DateTimeOffset NextCloseUtc);
