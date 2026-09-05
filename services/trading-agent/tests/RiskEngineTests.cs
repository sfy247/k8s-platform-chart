using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;
using Xunit;

namespace ClaudeTradingAgent.Tests;

public sealed class RiskEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 18, 30, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<string> Symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AAPL" };

    [Fact]
    public void Rejects_when_kill_switch_is_off()
    {
        var result = new RiskEngine().Evaluate(Signal(), State(), Policy(tradingEnabled: false), Symbols, Now);
        Assert.False(result.Approved);
        Assert.Equal("KILL_SWITCH", result.Code);
    }

    [Fact]
    public void Rejects_non_paper_endpoint()
    {
        var result = new RiskEngine().Evaluate(Signal(), State(isPaper: false), Policy(), Symbols, Now);
        Assert.False(result.Approved);
        Assert.Equal("NOT_PAPER", result.Code);
    }

    [Fact]
    public void Rejects_when_daily_loss_limit_reached()
    {
        var result = new RiskEngine().Evaluate(Signal(), State(dailyPnl: -3m), Policy(), Symbols, Now);
        Assert.False(result.Approved);
        Assert.Equal("DAILY_LOSS_LIMIT", result.Code);
    }

    [Fact]
    public void Rejects_duplicate_symbol_exposure()
    {
        var result = new RiskEngine().Evaluate(Signal(), State(hasOpenOrder: true), Policy(), Symbols, Now);
        Assert.False(result.Approved);
        Assert.Equal("DUPLICATE_EXPOSURE", result.Code);
    }

    [Fact]
    public void Rejects_sell_that_would_open_short_position()
    {
        var sell = Signal() with { Action = TradeAction.Sell };
        var result = new RiskEngine().Evaluate(sell, State(), Policy(), Symbols, Now);
        Assert.False(result.Approved);
        Assert.Equal("NO_LONG_POSITION", result.Code);
    }

    [Fact]
    public void Approves_valid_paper_trade()
    {
        var result = new RiskEngine().Evaluate(Signal(), State(), Policy(), Symbols, Now);
        Assert.True(result.Approved);
        Assert.NotNull(result.Order);
        Assert.Equal(10m, result.Order!.Notional);
    }

    private static StrategySignal Signal() => new("AAPL", TradeAction.Buy, 10m, 0.8m, "momentum-v1", "test", Now);

    // ── Day trading: entries and exits are judged differently ────────────

    [Fact]
    public void Rejects_entry_when_the_pattern_day_trader_limit_is_reached()
    {
        var state = State(equity: 20_000m, dayTrades: 3);
        var result = new RiskEngine().Evaluate(Signal(), state, Policy(), Symbols, Now);
        Assert.False(result.Approved);
        Assert.Equal("PDT_LIMIT", result.Code);
    }

    [Fact]
    public void Allows_entry_at_the_day_trade_limit_when_equity_is_above_the_threshold()
    {
        var state = State(equity: 100_000m, dayTrades: 9);
        var result = new RiskEngine().Evaluate(Signal(), state, Policy(), Symbols, Now);
        Assert.True(result.Approved);
    }

    [Fact]
    public void Allows_entry_above_the_pdt_threshold_without_a_day_trade_count()
    {
        // Alpaca does not return daytrade_count for every account type.
        // Above the equity threshold the rule does not apply, so requiring
        // the field would fail every cycle — and take the flatten with it.
        var state = State(equity: 100_000m, dayTrades: null);
        Assert.True(new RiskEngine().Evaluate(Signal(), state, Policy(), Symbols, Now).Approved);
    }

    [Fact]
    public void Refuses_entry_below_the_pdt_threshold_without_a_day_trade_count()
    {
        // Here the limit does apply and cannot be evaluated, so fail closed.
        var result = new RiskEngine().Evaluate(
            Signal(), State(equity: 20_000m, dayTrades: null), Policy(), Symbols, Now);
        Assert.False(result.Approved);
        Assert.Equal("PDT_COUNT_UNKNOWN", result.Code);
    }

    [Fact]
    public void Still_allows_an_exit_without_a_day_trade_count()
    {
        var state = State(equity: 20_000m, dayTrades: null, position: 10m);
        Assert.True(new RiskEngine()
            .Evaluate(SellAll(), state, Policy(), Symbols, Now, OrderIntent.Exit).Approved);
    }

    [Fact]
    public void Rejects_entry_when_equity_is_unknown()
    {
        var result = new RiskEngine().Evaluate(Signal(), State(equity: 0m), Policy(), Symbols, Now);
        Assert.False(result.Approved);
        Assert.Equal("EQUITY_UNKNOWN", result.Code);
    }

    [Fact]
    public void Allows_an_exit_after_the_daily_loss_limit_is_reached()
    {
        // The whole point of separating intent: an agent that has hit its
        // loss limit must still be able to close the position that caused it,
        // or the risk control becomes a trap that holds the trade overnight.
        var state = State(dailyPnl: -50m, position: 10m);
        var result = new RiskEngine().Evaluate(SellAll(), state, Policy(), Symbols, Now, OrderIntent.Exit);
        Assert.True(result.Approved);
        Assert.Equal(OrderIntent.Exit, result.Order!.Intent);
    }

    [Fact]
    public void Allows_an_exit_after_the_daily_order_limit_is_reached()
    {
        var state = State(position: 10m, totalOrders: 999, symbolOrders: 999);
        var result = new RiskEngine().Evaluate(SellAll(), state, Policy(), Symbols, Now, OrderIntent.Exit);
        Assert.True(result.Approved);
    }

    [Fact]
    public void Allows_an_exit_at_the_pattern_day_trader_limit()
    {
        var state = State(equity: 20_000m, dayTrades: 3, position: 10m);
        var result = new RiskEngine().Evaluate(SellAll(), state, Policy(), Symbols, Now, OrderIntent.Exit);
        Assert.True(result.Approved);
    }

    [Fact]
    public void Allows_an_exit_on_stale_quote_data()
    {
        // An exit is driven by the broker's position state, not by a quote,
        // so quote age is not a reason to keep the position.
        var stale = SellAll() with { DataTimestampUtc = Now.AddHours(-3) };
        var result = new RiskEngine().Evaluate(stale, State(position: 10m), Policy(), Symbols, Now, OrderIntent.Exit);
        Assert.True(result.Approved);
    }

    [Fact]
    public void Allows_an_exit_larger_than_the_per_position_limit()
    {
        // A position can drift above the entry cap while it is held. Refusing
        // to close it because it grew would be exactly backwards.
        var big = SellAll() with { ProposedNotional = 45m };
        var result = new RiskEngine().Evaluate(big, State(position: 45m), Policy(), Symbols, Now, OrderIntent.Exit);
        Assert.True(result.Approved);
    }

    [Fact]
    public void Refuses_an_exit_while_a_close_is_already_working()
    {
        var state = State(position: 10m, hasOpenOrder: true);
        var result = new RiskEngine().Evaluate(SellAll(), state, Policy(), Symbols, Now, OrderIntent.Exit);
        Assert.False(result.Approved);
        Assert.Equal("DUPLICATE_EXPOSURE", result.Code);
    }

    [Fact]
    public void Refuses_an_exit_when_the_kill_switch_is_off()
    {
        var result = new RiskEngine().Evaluate(
            SellAll(), State(position: 10m), Policy(tradingEnabled: false), Symbols, Now, OrderIntent.Exit);
        Assert.False(result.Approved);
        Assert.Equal("KILL_SWITCH", result.Code);
    }

    [Fact]
    public void Refuses_an_exit_when_the_market_is_closed()
    {
        var result = new RiskEngine().Evaluate(
            SellAll(), State(position: 10m, marketOpen: false), Policy(), Symbols, Now, OrderIntent.Exit);
        Assert.False(result.Approved);
        Assert.Equal("MARKET_CLOSED", result.Code);
    }

    private static StrategySignal SellAll() => new("AAPL", TradeAction.Sell, 10m, 1.0m, "exit-manager", "flatten", Now);

    private static RiskPolicy Policy(bool tradingEnabled = true) =>
        new(10m, 3, 3m, 10m, 30m, 2, 8, TimeSpan.FromSeconds(15), true, tradingEnabled,
            PdtEquityThreshold: 25_000m, MaxDayTradesUnderPdt: 3);

    private static AccountRiskState State(
        bool isPaper = true,
        decimal dailyPnl = 0m,
        bool hasOpenOrder = false,
        decimal equity = 100_000m,
        int? dayTrades = 0,
        decimal position = 0m,
        int totalOrders = 0,
        int symbolOrders = 0,
        bool marketOpen = true) =>
        new(100m, 0m, dailyPnl, 0, totalOrders, symbolOrders, marketOpen, isPaper, hasOpenOrder, position,
            Equity: equity, DayTradeCount: dayTrades);
}
