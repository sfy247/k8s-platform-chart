using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeTradingAgent.RiskManagement;

namespace ClaudeTradingAgent.TradingAgent.Hosting;

/// <summary>
/// Reads account, position and order state from the paper broker.
///
/// The risk engine refuses to approve anything without this, and it must
/// never be guessed: an invented cash balance or position count would let a
/// limit be breached silently. Per-position P&L is read here rather than
/// remembered locally, so a restarted pod still knows where its stops are.
/// </summary>
public sealed class AccountSnapshotProvider
{
    private readonly HttpClient _http;
    private readonly string _tradingBaseUrl;

    public AccountSnapshotProvider(HttpClient http, string apiKey, string apiSecret, string tradingBaseUrl)
    {
        _http = http;
        _tradingBaseUrl = tradingBaseUrl.TrimEnd('/');
        _http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", apiKey);
        _http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", apiSecret);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public sealed record Snapshot(
        decimal Cash,
        decimal Equity,
        decimal PortfolioExposure,
        decimal DayPnl,
        int DayTradeCount,
        IReadOnlyList<PositionSnapshot> Positions,
        IReadOnlyDictionary<string, decimal> PositionNotionalBySymbol)
    {
        public int OpenPositionCount => Positions.Count;
    }

    /// <summary>One of today's orders. Side and fill time are what let an open position be dated.</summary>
    public sealed record OrderSnapshot(string Symbol, string Status, string Side, DateTimeOffset? FilledAtUtc);

    public async Task<Snapshot> GetAsync(CancellationToken cancellationToken)
    {
        var account = await GetJsonAsync($"{_tradingBaseUrl}/v2/account", cancellationToken);
        var cash = ParseDecimal(account, "cash");
        var equity = ParseDecimal(account, "equity");

        // Today's profit and loss, against the previous session's close.
        // This is total P&L rather than realised only, which is the more
        // conservative measure for a daily loss limit: an open position that
        // is down counts against the budget before it is closed, not after.
        var dayPnl = equity - ParseDecimal(account, "last_equity");

        // Day trades used in the rolling five-business-day window, as the
        // broker counts them. The agent never derives this itself — the
        // broker's count is the one that triggers the restriction.
        var dayTradeCount = ParseInt(account, "daytrade_count");

        var positions = await GetJsonAsync($"{_tradingBaseUrl}/v2/positions", cancellationToken);
        var list = new List<PositionSnapshot>();
        var bySymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        decimal exposure = 0m;

        if (positions.ValueKind == JsonValueKind.Array)
        {
            foreach (var position in positions.EnumerateArray())
            {
                var symbol = position.GetProperty("symbol").GetString();
                if (string.IsNullOrWhiteSpace(symbol)) continue;

                var marketValue = Math.Abs(ParseDecimal(position, "market_value"));

                // Quantity and market value are strict: without them there is
                // no position to reason about. The P&L fields are optional so
                // that a broker response missing one suspends the stop rather
                // than failing the cycle — a failed cycle also skips the
                // end-of-day flatten, which is the worse outcome by far.
                list.Add(new PositionSnapshot(
                    symbol.ToUpperInvariant(),
                    ParseDecimal(position, "qty"),
                    marketValue,
                    ParseOptionalDecimal(position, "avg_entry_price"),
                    ParseOptionalDecimal(position, "current_price"),
                    ParseOptionalDecimal(position, "unrealized_pl"),
                    ParseOptionalDecimal(position, "unrealized_plpc")));

                bySymbol[symbol] = marketValue;
                exposure += marketValue;
            }
        }

        return new Snapshot(cash, equity, exposure, dayPnl, dayTradeCount, list, bySymbol);
    }

    /// <summary>Orders already placed today, for the daily rate limits and position dating.</summary>
    public async Task<IReadOnlyList<OrderSnapshot>> GetTodaysOrdersAsync(CancellationToken cancellationToken)
    {
        var after = DateTimeOffset.UtcNow.Date.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var root = await GetJsonAsync($"{_tradingBaseUrl}/v2/orders?status=all&after={after}&limit=500", cancellationToken);

        if (root.ValueKind != JsonValueKind.Array) return [];

        return root.EnumerateArray()
            .Select(o => new OrderSnapshot(
                ReadString(o, "symbol"),
                ReadString(o, "status"),
                ReadString(o, "side"),
                ReadNullableTimestamp(o, "filled_at")))
            .Where(o => o.Symbol.Length > 0)
            .ToList();
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Alpaca returned HTTP {(int)response.StatusCode} for {new Uri(url).AbsolutePath}.");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static DateTimeOffset? ReadNullableTimestamp(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts)
            ? ts
            : null;

    private static decimal ParseDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            throw new InvalidOperationException($"Broker field '{property}' is missing.");

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => throw new InvalidOperationException($"Broker field '{property}' is not numeric."),
        };
    }

    /// <summary>Null when the broker did not send a usable number. Never zero as a stand-in.</summary>
    private static decimal? ParseOptionalDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static int ParseInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            throw new InvalidOperationException($"Broker field '{property}' is missing.");

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var i) => i,
            _ => throw new InvalidOperationException($"Broker field '{property}' is not an integer."),
        };
    }
}
