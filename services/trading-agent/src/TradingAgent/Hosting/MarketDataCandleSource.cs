using ClaudeTradingAgent.Charting;
using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.TradingAgent.Hosting;

/// <summary>Adapts the read-only market data provider to the charting layer's candle source.</summary>
public sealed class MarketDataCandleSource(IMarketDataProvider market) : ICandleSource
{
    public async Task<IReadOnlyList<Candle>> GetOneMinuteCandlesAsync(string symbol, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        var bars = await market.GetOneMinuteBarsAsync(symbol, startUtc, endUtc, cancellationToken);
        return bars.Select(b => Candle.FromBar(symbol, b)).ToList();
    }
}
