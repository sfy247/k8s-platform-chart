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

    private static RiskPolicy Policy(bool tradingEnabled = true) => new(10m, 3, 3m, 10m, 30m, 2, 8, TimeSpan.FromSeconds(15), true, tradingEnabled);

    private static AccountRiskState State(bool isPaper = true, decimal dailyPnl = 0m, bool hasOpenOrder = false) =>
        new(100m, 0m, dailyPnl, 0, 0, 0, true, isPaper, hasOpenOrder, 0m);
}
