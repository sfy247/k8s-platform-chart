namespace ClaudeTradingAgent.MarketData;

/// <summary>Why a quote was refused. A label, so it can be counted rather than grepped for.</summary>
public enum MarketDataRejection
{
    Unavailable,
    NonPositivePrice,
    CrossedQuote,
    Stale,
    WideSpread,
}

/// <summary>
/// A quote the agent will not trade on.
///
/// Derives from InvalidOperationException so existing fail-closed handlers
/// keep catching it, but carries the reason as data. The distinction matters:
/// a wide spread on a liquid name usually means the data feed is thin, while
/// a stale quote usually means the connection is. Those need different
/// responses, and a string in a log message cannot tell them apart at scale.
/// </summary>
public sealed class MarketDataException(MarketDataRejection reason, string message)
    : InvalidOperationException(message)
{
    public MarketDataRejection Reason { get; } = reason;
}

public static class MarketDataValidator
{
    public static void ValidateQuote(QuoteSnapshot quote, DateTimeOffset now, TimeSpan maxAge, decimal maxSpreadBps)
    {
        if (quote.Bid <= 0 || quote.Ask <= 0)
            throw new MarketDataException(MarketDataRejection.NonPositivePrice, "Quote contains a non-positive bid/ask.");

        if (quote.Bid > quote.Ask)
            throw new MarketDataException(MarketDataRejection.CrossedQuote, "Quote bid exceeds ask.");

        if (now - quote.TimestampUtc > maxAge)
            throw new MarketDataException(MarketDataRejection.Stale, "Quote is stale.");

        if (quote.SpreadBps > maxSpreadBps)
            throw new MarketDataException(MarketDataRejection.WideSpread,
                $"Quote spread {quote.SpreadBps:0.0}bps exceeds the {maxSpreadBps:0}bps policy.");
    }
}
