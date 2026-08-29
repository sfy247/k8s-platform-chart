using System.Net;
using System.Text.Json;
using ClaudeTradingAgent.Execution;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;
using Xunit;

namespace TradingAgent.Tests;

/// <summary>
/// The order-submission path had never executed. These pin its behaviour
/// before it is allowed to place real paper orders — a bug found here costs
/// nothing, the same bug found at 09:31 on a Monday costs an unexplained
/// order.
/// </summary>
public sealed class AlpacaPaperOrderExecutorTests
{
    private const string Base = "https://paper-api.alpaca.markets";

    private static ApprovedOrder Order(TradeAction action = TradeAction.Buy) =>
        new("cta-20260831-AAPL-abc", "AAPL", action, 10m, DateTimeOffset.UtcNow);

    private static string OrderJson(string id = "brk-1", string status = "accepted") => $$"""
        {"id":"{{id}}","client_order_id":"cta-20260831-AAPL-abc","symbol":"AAPL",
         "status":"{{status}}","filled_qty":"0","filled_avg_price":null,
         "submitted_at":"2026-08-31T13:31:00Z"}
        """;

    private sealed class Recorder
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
    }

    private static (AlpacaPaperOrderExecutor Executor, Recorder Log) Build(
        Func<HttpRequestMessage, Recorder, HttpResponseMessage> handler)
    {
        var log = new Recorder();
        var transport = new StubHandler(async (req, ct) =>
        {
            log.Requests.Add(req);
            log.Bodies.Add(req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct));
            return handler(req, log);
        });
        var http = new HttpClient(transport);
        return (new AlpacaPaperOrderExecutor(http, "key", "secret", Base), log);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => f(r, ct);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task SubmitsANotionalMarketOrderWithTheApprovedValues()
    {
        var (executor, log) = Build((req, _) =>
            req.Method == HttpMethod.Get
                ? Json(HttpStatusCode.NotFound, "{}")      // no existing order
                : Json(HttpStatusCode.OK, OrderJson()));

        var result = await executor.SubmitApprovedOrderAsync(Order());

        var body = JsonDocument.Parse(log.Bodies.Last(Item => Item.Length > 0)).RootElement;
        Assert.Equal("AAPL", body.GetProperty("symbol").GetString());
        Assert.Equal("10.00", body.GetProperty("notional").GetString());
        Assert.Equal("buy", body.GetProperty("side").GetString());
        Assert.Equal("market", body.GetProperty("type").GetString());
        Assert.Equal("day", body.GetProperty("time_in_force").GetString());
        // Extended hours must stay off: the project forbids it, and a
        // notional market order is not accepted outside regular hours anyway.
        Assert.False(body.GetProperty("extended_hours").GetBoolean());
        Assert.Equal("cta-20260831-AAPL-abc", body.GetProperty("client_order_id").GetString());

        Assert.Equal("brk-1", result.BrokerOrderId);
        Assert.Equal("accepted", result.Status);
    }

    [Fact]
    public async Task ChecksForAnExistingOrderBeforeSubmitting()
    {
        var (executor, log) = Build((req, _) =>
            req.Method == HttpMethod.Get
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.OK, OrderJson()));

        await executor.SubmitApprovedOrderAsync(Order());

        // The idempotency lookup must come first, or a retry places a second
        // order before discovering the first.
        Assert.Equal(HttpMethod.Get, log.Requests[0].Method);
        Assert.Contains("by_client_order_id", log.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task ReturnsTheExistingOrderInsteadOfPlacingADuplicate()
    {
        var posted = false;
        var (executor, _) = Build((req, _) =>
        {
            if (req.Method == HttpMethod.Post) { posted = true; return Json(HttpStatusCode.OK, OrderJson("brk-new")); }
            return Json(HttpStatusCode.OK, OrderJson("brk-existing", "filled"));
        });

        var result = await executor.SubmitApprovedOrderAsync(Order());

        Assert.False(posted);                       // nothing was submitted
        Assert.Equal("brk-existing", result.BrokerOrderId);
        Assert.Equal("filled", result.Status);
    }

    [Fact]
    public async Task SendsAuthenticationHeaders()
    {
        var (executor, log) = Build((req, _) =>
            req.Method == HttpMethod.Get ? Json(HttpStatusCode.NotFound, "{}") : Json(HttpStatusCode.OK, OrderJson()));

        await executor.SubmitApprovedOrderAsync(Order());

        Assert.True(log.Requests[0].Headers.Contains("APCA-API-KEY-ID"));
        Assert.True(log.Requests[0].Headers.Contains("APCA-API-SECRET-KEY"));
    }

    [Fact]
    public async Task ARejectedOrderThrowsRatherThanReportingSuccess()
    {
        var (executor, _) = Build((req, _) =>
            req.Method == HttpMethod.Get
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.UnprocessableEntity, """{"message":"insufficient buying power"}"""));

        await Assert.ThrowsAsync<HttpRequestException>(() => executor.SubmitApprovedOrderAsync(Order()));
    }

    [Fact]
    public async Task AFailedIdempotencyCheckAbortsRatherThanSubmittingBlind()
    {
        var posted = false;
        var (executor, _) = Build((req, _) =>
        {
            if (req.Method == HttpMethod.Post) { posted = true; return Json(HttpStatusCode.OK, OrderJson()); }
            return Json(HttpStatusCode.InternalServerError, "{}");
        });

        // If we cannot tell whether the order already exists, submitting
        // risks a duplicate. Failing closed is correct.
        await Assert.ThrowsAsync<HttpRequestException>(() => executor.SubmitApprovedOrderAsync(Order()));
        Assert.False(posted);
    }

    [Fact]
    public async Task RefusesToExecuteAHoldAction()
    {
        var (executor, _) = Build((_, _) => Json(HttpStatusCode.OK, OrderJson()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.SubmitApprovedOrderAsync(Order(TradeAction.Hold)));
    }

    [Fact]
    public void RefusesANonPaperEndpoint()
    {
        // The guard is the last line between paper and live trading.
        Assert.Throws<InvalidOperationException>(
            () => new AlpacaPaperOrderExecutor(new HttpClient(), "k", "s", "https://api.alpaca.markets"));
    }
}
