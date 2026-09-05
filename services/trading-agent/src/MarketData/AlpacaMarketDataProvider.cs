using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeTradingAgent.MarketData;

/// <summary>
/// Read-only Alpaca market data. This type never places, amends or cancels
/// an order — it cannot, it only issues GETs against the data, clock and
/// calendar endpoints. Execution lives behind IOrderExecutor and the risk
/// engine.
///
/// The data feed is injected rather than hardcoded. Which venues a quote is
/// built from decides how often the agent sees a spread it will not trade,
/// so it is a deployment decision that should be visible, not a constant
/// buried in a query string.
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
    private readonly string _feed;

    /// <summary>
    /// Session bounds change once a day, so they are cached across the
    /// per-cycle DI scope. Keyed by exchange date, which is what a calendar
    /// entry is actually about.
    /// </summary>
    private static readonly ConcurrentDictionary<DateOnly, TradingSession> SessionCache = new();

    public AlpacaMarketDataProvider(
        HttpClient http, string apiKey, string apiSecret, string dataBaseUrl, string tradingBaseUrl, string feed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(feed);

        _http = http;
        _dataBaseUrl = dataBaseUrl.TrimEnd('/');
        _tradingBaseUrl = tradingBaseUrl.TrimEnd('/');
        _feed = feed.Trim().ToLowerInvariant();

        _http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", apiKey);
        _http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", apiSecret);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<MarketClock> GetMarketClockAsync(CancellationToken cancellationToken = default)
    {
        var root = await GetJsonAsync($"{_tradingBaseUrl}/v2/clock", cancellationToken);

        // Parsed with the offset intact rather than normalised to UTC: the
        // offset IS the answer to "what timezone is the exchange in right
        // now", including across a DST switch, and it saves depending on a
        // timezone database being present in the container image.
        var stamp = ReadOffsetTimestamp(root, "timestamp");

        return new MarketClock(
            root.GetProperty("is_open").GetBoolean(),
            stamp.ToUniversalTime(),
            ReadTimestamp(root, "next_open"),
            ReadTimestamp(root, "next_close"),
            stamp.Offset);
    }

    public async Task<TradingSession> GetSessionAsync(MarketClock clock, CancellationToken cancellationToken = default)
    {
        // The exchange date, not the UTC date. After 20:00 UTC these differ,
        // which is precisely the part of the day a day-trading agent cares
        // about most.
        var exchangeDate = DateOnly.FromDateTime(
            (clock.TimestampUtc.ToOffset(clock.ExchangeOffset)).DateTime);

        if (SessionCache.TryGetValue(exchangeDate, out var cached)) return cached;

        var iso = exchangeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var root = await GetJsonAsync($"{_tradingBaseUrl}/v2/calendar?start={iso}&end={iso}", cancellationToken);

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidOperationException($"No exchange calendar entry for {iso}; the session bounds are unknown.");

        var entry = root[0];
        var session = new TradingSession(
            exchangeDate,
            CombineExchangeLocal(exchangeDate, ReadString(entry, "open"), clock.ExchangeOffset),
            CombineExchangeLocal(exchangeDate, ReadString(entry, "close"), clock.ExchangeOffset));

        if (session.CloseUtc <= session.OpenUtc)
            throw new InvalidOperationException($"Exchange calendar for {iso} has a close at or before its open.");

        SessionCache[exchangeDate] = session;
        return session;
    }

    public async Task<QuoteSnapshot> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        // The feed is stated on every request. Left implicit it falls back to
        // whatever the account plan defaults to, which means the quality of
        // the data the agent trades on becomes invisible in the code.
        var root = await GetJsonAsync(
            $"{_dataBaseUrl}/v2/stocks/{Uri.EscapeDataString(symbol)}/quotes/latest?feed={_feed}", cancellationToken);

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
                  + $"?timeframe=1Min&limit={limit}&feed={_feed}&sort=desc";
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

    /// <summary>Turns a calendar "09:30" in exchange-local time into an absolute instant.</summary>
    private static DateTimeOffset CombineExchangeLocal(DateOnly date, string hhmm, TimeSpan exchangeOffset)
    {
        if (!TimeOnly.TryParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new InvalidOperationException($"Calendar time '{hhmm}' is not in HH:mm form.");

        return new DateTimeOffset(date.ToDateTime(time), exchangeOffset).ToUniversalTime();
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidOperationException($"Field '{property}' is missing or not a string.");

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

    /// <summary>Like <see cref="ReadTimestamp"/> but keeps the sender's UTC offset.</summary>
    private static DateTimeOffset ReadOffsetTimestamp(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out var ts)
            ? ts
            : throw new InvalidOperationException($"Field '{property}' is missing or not a timestamp.");
}
