using ClaudeTradingAgent.Strategy;
using Xunit;

namespace ClaudeTradingAgent.Tests;

public sealed class MomentumStrategyTests
{
    [Fact]
    public void Holds_when_data_is_stale()
    {
        var now = new DateTimeOffset(2026, 8, 24, 18, 30, 0, TimeSpan.Zero);
        var input = new MomentumInputs("AAPL", 101m, 100m, 99m, 1.5m, 8m, now.AddMinutes(-1));
        var policy = new MomentumPolicy(0.70m, 1.20m, 25m, 10m, TimeSpan.FromSeconds(15));

        var result = new MomentumStrategy().Evaluate(input, policy, now);

        Assert.Equal(TradeAction.Hold, result.Action);
    }

    [Fact]
    public void Holds_when_spread_is_too_wide()
    {
        var now = DateTimeOffset.UtcNow;
        var input = new MomentumInputs("AAPL", 101m, 100m, 99m, 1.5m, 40m, now);
        var policy = new MomentumPolicy(0.70m, 1.20m, 25m, 10m, TimeSpan.FromSeconds(15));

        var result = new MomentumStrategy().Evaluate(input, policy, now);

        Assert.Equal(TradeAction.Hold, result.Action);
    }
}
