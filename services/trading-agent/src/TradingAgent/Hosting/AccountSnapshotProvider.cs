using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeTradingAgent.RiskManagement;

namespace ClaudeTradingAgent.TradingAgent.Hosting;

/// <summary>
/// Reads account and position state from the paper broker.
///
/// The risk engine refuses to approve anything without this, and it must
/// never be guessed: an invented cash balance or position count would let a
/// limit be breached silently.
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
        decimal PortfolioExposure,
        int OpenPositionCount,
        IReadOnlyDictionary<string, decimal> PositionNotionalBySymbol);

    public async Task<Snapshot> GetAsync(CancellationToken cancellationToken)
    {
        var account = await GetJsonAsync($"{_tradingBaseUrl}/v2/account", cancellationToken);
        var cash = ParseDecimal(account, "cash");

        var positions = await GetJsonAsync($"{_tradingBaseUrl}/v2/positions", cancellationToken);
        var bySymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        decimal exposure = 0m;

        if (positions.ValueKind == JsonValueKind.Array)
        {
            foreach (var position in positions.EnumerateArray())
            {
                var symbol = position.GetProperty("symbol").GetString();
                if (string.IsNullOrWhiteSpace(symbol)) continue;

                var marketValue = Math.Abs(ParseDecimal(position, "market_value"));
                bySymbol[symbol] = marketValue;
                exposure += marketValue;
            }
        }

        return new Snapshot(cash, exposure, bySymbol.Count, bySymbol);
    }

    /// <summary>Orders already placed today, for the daily rate limits.</summary>
    public async Task<IReadOnlyList<(string Symbol, string Status)>> GetTodaysOrdersAsync(CancellationToken cancellationToken)
    {
        var after = DateTimeOffset.UtcNow.Date.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var root = await GetJsonAsync($"{_tradingBaseUrl}/v2/orders?status=all&after={after}&limit=500", cancellationToken);

        if (root.ValueKind != JsonValueKind.Array) return [];

        return root.EnumerateArray()
            .Select(o => (
                Symbol: o.TryGetProperty("symbol", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                Status: o.TryGetProperty("status", out var st) ? st.GetString() ?? string.Empty : string.Empty))
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

    private static decimal ParseDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            throw new InvalidOperationException($"Account field '{property}' is missing.");

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => throw new InvalidOperationException($"Account field '{property}' is not numeric."),
        };
    }
}
