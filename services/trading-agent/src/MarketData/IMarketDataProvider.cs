namespace ClaudeTradingAgent.MarketData;

public interface IMarketDataProvider
{
    Task<QuoteSnapshot> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Bar>> GetRecentBarsAsync(string symbol, int limit, CancellationToken cancellationToken = default);
    /// <summary>
    /// 1-minute bars between two instants, oldest first, following pagination.
    /// Read-only; used for charting and analysis, never for order decisions in phase 1.
    /// </summary>
    Task<IReadOnlyList<Bar>> GetOneMinuteBarsAsync(string symbol, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);

    Task<AssetMetadata> GetAssetAsync(string symbol, CancellationToken cancellationToken = default);
    Task<MarketClock> GetMarketClockAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The regular session bounds for the exchange date the clock is currently in.
    /// Required before any entry is taken: a day trader who does not know when
    /// the session ends cannot promise to be flat at the end of it.
    /// </summary>
    Task<TradingSession> GetSessionAsync(MarketClock clock, CancellationToken cancellationToken = default);
}
