using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.Strategy;

/// <summary>
/// Everything a strategy is given. Deliberately raw: each strategy computes
/// its own indicators from the same bars, so two strategies compared against
/// each other are being fed identical information.
/// </summary>
public sealed record StrategyInput(
    string Symbol,
    IReadOnlyList<Bar> Bars,          // oldest first, the newest is the signal bar
    decimal LastPrice,
    decimal SpreadBps,
    DateTimeOffset DataTimestampUtc);

/// <summary>
/// A strategy proposes; it never decides. Everything it returns still passes
/// through the deterministic risk engine, which is the only thing that can
/// approve an order.
/// </summary>
public interface ITradingStrategy
{
    string Name { get; }

    /// <summary>The premise — why this pattern might persist. Stated so it can be argued with.</summary>
    string Premise { get; }

    StrategySignal Evaluate(StrategyInput input, StrategyPolicy policy, DateTimeOffset now);
}

/// <summary>Shared limits. Individual strategies read only what they need.</summary>
public sealed record StrategyPolicy(
    decimal MinimumConfidence,
    decimal MinimumVolumeRatio,
    decimal MaximumSpreadBps,
    decimal MaxPositionNotional,
    TimeSpan MaxDataAge);

public static class StrategyHelpers
{
    public static StrategySignal Hold(StrategyInput input, string name, string reason) =>
        new(input.Symbol.ToUpperInvariant(), TradeAction.Hold, 0m, 0m, name, reason, input.DataTimestampUtc);

    /// <summary>Gate every strategy on the same data-quality rules, so a comparison compares strategies rather than tolerance for bad data.</summary>
    public static StrategySignal? RejectBadData(StrategyInput input, StrategyPolicy policy, string name, DateTimeOffset now)
    {
        if (input.LastPrice <= 0) return Hold(input, name, "Invalid price.");
        if (now - input.DataTimestampUtc > policy.MaxDataAge) return Hold(input, name, "Market data is stale.");
        if (input.SpreadBps > policy.MaximumSpreadBps) return Hold(input, name, "Spread exceeds policy.");
        return null;
    }

    public static decimal VolumeRatio(IReadOnlyList<Bar> bars, int fastWindow)
    {
        var volumes = bars.Select(b => (decimal)b.Volume).ToArray();
        if (volumes.Length == 0) return 0m;
        var recent = volumes.TakeLast(Math.Min(fastWindow, volumes.Length)).Average();
        var average = volumes.Average();
        return average <= 0 ? 0m : recent / average;
    }
}
