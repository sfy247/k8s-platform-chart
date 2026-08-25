namespace ClaudeTradingAgent.Strategy;

public sealed record MomentumInputs(
    string Symbol,
    decimal LastPrice,
    decimal FastAverage,
    decimal SlowAverage,
    decimal VolumeRatio,
    decimal SpreadBps,
    DateTimeOffset DataTimestampUtc);

public sealed record MomentumPolicy(
    decimal MinimumConfidence,
    decimal MinimumVolumeRatio,
    decimal MaximumSpreadBps,
    decimal MaxPositionNotional,
    TimeSpan MaxDataAge);

public sealed class MomentumStrategy
{
    public StrategySignal Evaluate(MomentumInputs input, MomentumPolicy policy, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(input.Symbol)) throw new ArgumentException("Symbol is required.", nameof(input));
        if (input.LastPrice <= 0) return Hold(input, "Invalid price.");
        if (now - input.DataTimestampUtc > policy.MaxDataAge) return Hold(input, "Market data is stale.");
        if (input.SpreadBps > policy.MaximumSpreadBps) return Hold(input, "Spread exceeds policy.");
        if (input.VolumeRatio < policy.MinimumVolumeRatio) return Hold(input, "Volume confirmation is insufficient.");

        var bullish = input.FastAverage > input.SlowAverage && input.LastPrice >= input.FastAverage;
        var bearish = input.FastAverage < input.SlowAverage && input.LastPrice <= input.FastAverage;

        if (!bullish && !bearish) return Hold(input, "No confirmed momentum setup.");

        var trendStrength = input.SlowAverage == 0 ? 0 : Math.Abs((input.FastAverage - input.SlowAverage) / input.SlowAverage);
        var confidence = Math.Clamp(0.60m + Math.Min(trendStrength * 10m, 0.15m) + Math.Min((input.VolumeRatio - 1m) * 0.10m, 0.10m), 0m, 0.95m);
        if (confidence < policy.MinimumConfidence) return Hold(input, "Signal confidence is below threshold.");

        return new StrategySignal(
            input.Symbol.ToUpperInvariant(),
            bullish ? TradeAction.Buy : TradeAction.Sell,
            policy.MaxPositionNotional,
            confidence,
            "momentum-v1",
            bullish ? "Momentum criteria confirmed." : "Negative momentum criteria confirmed.",
            input.DataTimestampUtc);
    }

    private static StrategySignal Hold(MomentumInputs input, string reason) =>
        new(input.Symbol.ToUpperInvariant(), TradeAction.Hold, 0m, 0m, "momentum-v1", reason, input.DataTimestampUtc);
}
