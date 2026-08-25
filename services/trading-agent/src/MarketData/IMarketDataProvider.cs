namespace ClaudeTradingAgent.MarketData;

public interface IMarketDataProvider
{
    Task<QuoteSnapshot> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Bar>> GetRecentBarsAsync(string symbol, int limit, CancellationToken cancellationToken = default);
    Task<AssetMetadata> GetAssetAsync(string symbol, CancellationToken cancellationToken = default);
    Task<MarketClock> GetMarketClockAsync(CancellationToken cancellationToken = default);
}
