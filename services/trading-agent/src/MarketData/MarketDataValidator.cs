namespace ClaudeTradingAgent.MarketData;

public static class MarketDataValidator
{
    public static void ValidateQuote(QuoteSnapshot quote, DateTimeOffset now, TimeSpan maxAge, decimal maxSpreadBps)
    {
        if (quote.Bid <= 0 || quote.Ask <= 0) throw new InvalidOperationException("Quote contains a non-positive bid/ask.");
        if (quote.Bid > quote.Ask) throw new InvalidOperationException("Quote bid exceeds ask.");
        if (now - quote.TimestampUtc > maxAge) throw new InvalidOperationException("Quote is stale.");
        if (quote.SpreadBps > maxSpreadBps) throw new InvalidOperationException("Quote spread exceeds policy.");
    }
}
