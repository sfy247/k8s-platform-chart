using System.Net.Http.Json;
using System.Text.Json;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.Execution;

public sealed class AlpacaPaperOrderExecutor(HttpClient httpClient, string apiKey, string apiSecret, string baseUrl) : IOrderExecutor
{
    private readonly HttpClient _http = Configure(httpClient, apiKey, apiSecret, baseUrl);

    public async Task<BrokerOrderResult> SubmitApprovedOrderAsync(ApprovedOrder order, CancellationToken cancellationToken = default)
    {
        if (order.Action is not (TradeAction.Buy or TradeAction.Sell))
            throw new InvalidOperationException("Only approved BUY or SELL actions may be executed.");

        var existing = await TryGetByClientOrderIdAsync(order.ClientOrderId, cancellationToken);
        if (existing is not null) return existing;

        var payload = new
        {
            symbol = order.Symbol,
            notional = order.Notional.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            side = order.Action == TradeAction.Buy ? "buy" : "sell",
            type = "market",
            time_in_force = "day",
            extended_hours = false,
            client_order_id = order.ClientOrderId
        };

        using var response = await _http.PostAsJsonAsync("/v2/orders", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Paper order rejected with HTTP {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new BrokerOrderResult(
            root.GetProperty("id").GetString() ?? throw new InvalidOperationException("Broker order id missing."),
            root.GetProperty("client_order_id").GetString() ?? order.ClientOrderId,
            root.GetProperty("symbol").GetString() ?? order.Symbol,
            root.GetProperty("status").GetString() ?? "unknown",
            ParseNullableDecimal(root, "filled_qty"),
            ParseNullableDecimal(root, "filled_avg_price"),
            ParseTimestamp(root, "submitted_at"));
    }

    private async Task<BrokerOrderResult?> TryGetByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"/v2/orders:by_client_order_id?client_order_id={Uri.EscapeDataString(clientOrderId)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Unable to verify order idempotency. HTTP {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new BrokerOrderResult(
            root.GetProperty("id").GetString() ?? throw new InvalidOperationException("Broker order id missing."),
            root.GetProperty("client_order_id").GetString() ?? clientOrderId,
            root.GetProperty("symbol").GetString() ?? string.Empty,
            root.GetProperty("status").GetString() ?? "unknown",
            ParseNullableDecimal(root, "filled_qty"),
            ParseNullableDecimal(root, "filled_avg_price"),
            ParseTimestamp(root, "submitted_at"));
    }

    private static HttpClient Configure(HttpClient client, string key, string secret, string baseUrl)
    {
        client.BaseAddress = PaperEndpointGuard.Validate(baseUrl);
        client.DefaultRequestHeaders.Remove("APCA-API-KEY-ID");
        client.DefaultRequestHeaders.Remove("APCA-API-SECRET-KEY");
        client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", key);
        client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", secret);
        return client;
    }

    private static decimal? ParseNullableDecimal(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return decimal.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset ParseTimestamp(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value) && DateTimeOffset.TryParse(value.GetString(), out var parsed)) return parsed;
        return DateTimeOffset.UtcNow;
    }
}
