using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeTradingAgent.MarketData;

/// <summary>
/// Read-only Alpaca market data. This type never places, amends or cancels
/// an order — it cannot, it only issues GETs against the data and clock
/// endpoints. Execution lives behind IOrderExecutor and the risk engine.
///
/// Every method fails loudly rather than returning a guess. Callers treat a
/// throw as "no trade": inferring a missing price is how a trading system
/// loses money quietly.
/// </summary>
public sealed class AlpacaMarketDataProvider : IMarketDataProvider
{
    private readonly HttpClient _http;
    private readonly string _dataBaseUrl;
    private readonly string _tradingBaseUrl;

    public AlpacaMarketDataProvider(HttpClient http, string apiKey, string apiSecret, string dataBaseUrl, string tradingBaseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiSecret);

        _http = http;
        _dataBaseUrl = dataBaseUrl.TrimEnd('/');
        _tradingBaseUrl = tradingBaseUrl.TrimEnd('/');

        _http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", apiKey);
        _http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", apiSecret);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<MarketClock> GetMarketClockAsync(CancellationToken cancellationToken = default)
    {
        var root = await GetJsonAsync($"{_tradingBaseUrl}/v2/clock", cancellationToken);
        return new MarketClock(
            root.GetProperty("is_open").GetBoolean(),
            ReadTimestamp(root, "timestamp"),
            ReadTimestamp(root, "next_open"),
            ReadTimestamp(root, "next_close"));
    }

    public async Task<QuoteSnapshot> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var root = await GetJsonAsync($"{_dataBaseUrl}/v2/stocks/{Uri.EscapeDataString(symbol)}/quotes/latest", cancellationToken);

        if (!root.TryGetProperty("quote", out var quote))
            throw new InvalidOperationException($"No quote returned for {symbol}.");

        return new QuoteSnapshot(
            symbol.ToUpperInvariant(),
            ReadDecimal(quote, "bp"),
            ReadDecimal(quote, "ap"),
            ReadTimestamp(quote, "t"));
    }

    public async Task<IReadOnlyList<Bar>> GetRecentBarsAsync(string symbol, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Bar limit must be between 1 and 1000.");

        var url = $"{_dataBaseUrl}/v2/stocks/{Uri.EscapeDataString(symbol)}/bars"
                  + $"?timeframe=1Min&limit={limit}&feed=iex&sort=desc";
        var root = await GetJsonAsync(url, cancellationToken);

        if (!root.TryGetProperty("bars", out var bars) || bars.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"No bars returned for {symbol}.");

        var result = bars.EnumerateArray()
            .Select(b => new Bar(
                ReadDecimal(b, "o"), ReadDecimal(b, "h"), ReadDecimal(b, "l"), ReadDecimal(b, "c"),
                b.GetProperty("v").GetInt64(), ReadTimestamp(b, "t")))
            .OrderBy(b => b.TimestampUtc)
            .ToList();

        if (result.Count == 0)
            throw new InvalidOperationException($"Bar series for {symbol} is empty.");

        return result;
    }

    public async Task<AssetMetadata> GetAssetAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var root = await GetJsonAsync($"{_tradingBaseUrl}/v2/assets/{Uri.EscapeDataString(symbol)}", cancellationToken);
        return new AssetMetadata(
            root.GetProperty("symbol").GetString() ?? symbol.ToUpperInvariant(),
            root.TryGetProperty("tradable", out var t) && t.GetBoolean(),
            root.TryGetProperty("fractionable", out var f) && f.GetBoolean(),
            root.TryGetProperty("class", out var c) ? c.GetString() ?? "us_equity" : "us_equity");
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // The body can echo request details; the status and URL path are
            // enough to diagnose, and never contain credentials.
            throw new HttpRequestException(
                $"Alpaca returned HTTP {(int)response.StatusCode} for {new Uri(url).AbsolutePath}.");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static decimal ReadDecimal(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDecimal(out var d)
            ? d
            : throw new InvalidOperationException($"Field '{property}' is missing or not numeric.");

    private static DateTimeOffset ReadTimestamp(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts)
            ? ts
            : throw new InvalidOperationException($"Field '{property}' is missing or not a timestamp.");
}
