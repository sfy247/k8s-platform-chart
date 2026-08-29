using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.Strategy;

// ─────────────────────────────────────────────────────────────────────────
// Four strategies with genuinely different premises. They are not variations
// on a theme: momentum bets that a move continues, mean reversion bets that
// it does not. If both appear profitable on the same data, that is evidence
// of overfitting rather than of two edges.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>The shipped strategy, behind the common interface so it can be compared.</summary>
public sealed class MomentumCrossoverStrategy : ITradingStrategy
{
    public string Name => "momentum-crossover";
    public string Premise =>
        "A short average crossing above a long one marks a trend that continues long enough to trade.";

    public StrategySignal Evaluate(StrategyInput input, StrategyPolicy policy, DateTimeOffset now)
    {
        if (StrategyHelpers.RejectBadData(input, policy, Name, now) is { } reject) return reject;

        var closes = input.Bars.Select(b => b.Close).ToArray();
        if (closes.Length < 5) return StrategyHelpers.Hold(input, Name, "Not enough history.");

        var fast = closes.TakeLast(5).Average();
        var slow = closes.Average();
        var volumeRatio = StrategyHelpers.VolumeRatio(input.Bars, 5);

        if (volumeRatio < policy.MinimumVolumeRatio)
            return StrategyHelpers.Hold(input, Name, "Volume confirmation is insufficient.");

        var bullish = fast > slow && input.LastPrice >= fast;
        var bearish = fast < slow && input.LastPrice <= fast;
        if (!bullish && !bearish) return StrategyHelpers.Hold(input, Name, "No confirmed momentum setup.");

        var strength = slow == 0 ? 0 : Math.Abs((fast - slow) / slow);
        var confidence = Math.Clamp(
            0.60m + Math.Min(strength * 10m, 0.15m) + Math.Min((volumeRatio - 1m) * 0.10m, 0.10m), 0m, 0.95m);
        if (confidence < policy.MinimumConfidence)
            return StrategyHelpers.Hold(input, Name, "Confidence below threshold.");

        return new StrategySignal(
            input.Symbol.ToUpperInvariant(),
            bullish ? TradeAction.Buy : TradeAction.Sell,
            policy.MaxPositionNotional, confidence, Name,
            bullish ? "Momentum confirmed." : "Negative momentum confirmed.",
            input.DataTimestampUtc);
    }
}

/// <summary>
/// The opposite bet to momentum: that short-term moves overshoot and snap
/// back. The premise is real — liquidity providers are compensated for
/// absorbing one-sided flow, and that compensation shows up as reversion —
/// but it is also the most crowded retail idea in existence.
/// </summary>
public sealed class RsiMeanReversionStrategy(int period = 14, decimal oversold = 30m, decimal overbought = 70m)
    : ITradingStrategy
{
    public string Name => "rsi-mean-reversion";
    public string Premise =>
        "Short-term moves overshoot; an oversold reading reverts before it continues.";

    public StrategySignal Evaluate(StrategyInput input, StrategyPolicy policy, DateTimeOffset now)
    {
        if (StrategyHelpers.RejectBadData(input, policy, Name, now) is { } reject) return reject;

        var closes = input.Bars.Select(b => b.Close).ToArray();
        if (closes.Length < period + 1) return StrategyHelpers.Hold(input, Name, "Not enough history for RSI.");

        var rsi = Rsi(closes, period);

        // Buying oversold; selling is exiting into strength, never shorting —
        // the risk engine blocks shorts, and the strategy should not propose
        // what the engine will always reject.
        if (rsi <= oversold)
        {
            var confidence = Math.Clamp(0.60m + (oversold - rsi) / 100m, 0m, 0.95m);
            if (confidence < policy.MinimumConfidence)
                return StrategyHelpers.Hold(input, Name, $"RSI {rsi:F1} oversold but confidence below threshold.");
            return new StrategySignal(input.Symbol.ToUpperInvariant(), TradeAction.Buy,
                policy.MaxPositionNotional, confidence, Name, $"RSI {rsi:F1} oversold.", input.DataTimestampUtc);
        }

        if (rsi >= overbought)
        {
            var confidence = Math.Clamp(0.60m + (rsi - overbought) / 100m, 0m, 0.95m);
            if (confidence < policy.MinimumConfidence)
                return StrategyHelpers.Hold(input, Name, $"RSI {rsi:F1} overbought but confidence below threshold.");
            return new StrategySignal(input.Symbol.ToUpperInvariant(), TradeAction.Sell,
                policy.MaxPositionNotional, confidence, Name, $"RSI {rsi:F1} overbought.", input.DataTimestampUtc);
        }

        return StrategyHelpers.Hold(input, Name, $"RSI {rsi:F1} is mid-range.");
    }

    /// <summary>Wilder's RSI.</summary>
    private static decimal Rsi(decimal[] closes, int period)
    {
        decimal gain = 0, loss = 0;
        for (var i = closes.Length - period; i < closes.Length; i++)
        {
            var change = closes[i] - closes[i - 1];
            if (change > 0) gain += change; else loss -= change;
        }
        var avgGain = gain / period;
        var avgLoss = loss / period;
        if (avgLoss == 0) return 100m;
        var rs = avgGain / avgLoss;
        return 100m - 100m / (1m + rs);
    }
}

/// <summary>
/// Price stretched far from the session's volume-weighted average price is
/// expected to return to it. VWAP is a genuine reference point because large
/// institutional orders are benchmarked against it, so there is real flow
/// pulling price back toward it — which is a more concrete premise than most.
/// </summary>
public sealed class VwapReversionStrategy(decimal entryDeviationPct = 0.4m) : ITradingStrategy
{
    public string Name => "vwap-reversion";
    public string Premise =>
        "Institutional orders are benchmarked to VWAP, so flow pulls price back toward it.";

    public StrategySignal Evaluate(StrategyInput input, StrategyPolicy policy, DateTimeOffset now)
    {
        if (StrategyHelpers.RejectBadData(input, policy, Name, now) is { } reject) return reject;

        // Only the current session: VWAP resets each day, and carrying it
        // across days makes it meaningless.
        var day = input.Bars[^1].TimestampUtc.UtcDateTime.Date;
        var session = input.Bars.Where(b => b.TimestampUtc.UtcDateTime.Date == day).ToList();
        if (session.Count < 5) return StrategyHelpers.Hold(input, Name, "Too little of the session has elapsed.");

        decimal pv = 0, volume = 0;
        foreach (var bar in session)
        {
            var typical = (bar.High + bar.Low + bar.Close) / 3m;
            pv += typical * bar.Volume;
            volume += bar.Volume;
        }
        if (volume <= 0) return StrategyHelpers.Hold(input, Name, "No volume in the session.");

        var vwap = pv / volume;
        var deviation = (input.LastPrice - vwap) / vwap * 100m;

        if (deviation <= -entryDeviationPct)
        {
            var confidence = Math.Clamp(0.60m + Math.Min(Math.Abs(deviation) / 10m, 0.30m), 0m, 0.95m);
            if (confidence < policy.MinimumConfidence)
                return StrategyHelpers.Hold(input, Name, $"{deviation:F2}% below VWAP, confidence below threshold.");
            return new StrategySignal(input.Symbol.ToUpperInvariant(), TradeAction.Buy,
                policy.MaxPositionNotional, confidence, Name,
                $"{deviation:F2}% below VWAP.", input.DataTimestampUtc);
        }

        // Back at or above VWAP: the reversion has happened, take the exit.
        if (deviation >= 0)
            return new StrategySignal(input.Symbol.ToUpperInvariant(), TradeAction.Sell,
                policy.MaxPositionNotional, 0.70m, Name,
                $"Reverted to VWAP ({deviation:F2}%).", input.DataTimestampUtc);

        return StrategyHelpers.Hold(input, Name, $"{deviation:F2}% from VWAP, inside the entry band.");
    }
}

/// <summary>
/// Buys a break above the range established in the first part of the session.
/// The premise is that overnight information is resolved in the opening
/// auction, and a decisive break of that range marks the day's direction.
/// </summary>
public sealed class OpeningRangeBreakoutStrategy(int rangeBars = 6) : ITradingStrategy
{
    public string Name => "opening-range-breakout";
    public string Premise =>
        "Overnight information resolves in the opening range; a break of it sets the day's direction.";

    public StrategySignal Evaluate(StrategyInput input, StrategyPolicy policy, DateTimeOffset now)
    {
        if (StrategyHelpers.RejectBadData(input, policy, Name, now) is { } reject) return reject;

        var day = input.Bars[^1].TimestampUtc.UtcDateTime.Date;
        var session = input.Bars.Where(b => b.TimestampUtc.UtcDateTime.Date == day).ToList();

        if (session.Count <= rangeBars)
            return StrategyHelpers.Hold(input, Name, "Opening range is still forming.");

        var opening = session.Take(rangeBars).ToList();
        var high = opening.Max(b => b.High);
        var low = opening.Min(b => b.Low);
        if (high <= low) return StrategyHelpers.Hold(input, Name, "Degenerate opening range.");

        var volumeRatio = StrategyHelpers.VolumeRatio(session, 3);
        if (volumeRatio < policy.MinimumVolumeRatio)
            return StrategyHelpers.Hold(input, Name, "Break lacks volume confirmation.");

        if (input.LastPrice > high)
        {
            var extension = (input.LastPrice - high) / (high - low);
            var confidence = Math.Clamp(0.60m + Math.Min(extension, 0.30m), 0m, 0.95m);
            if (confidence < policy.MinimumConfidence)
                return StrategyHelpers.Hold(input, Name, "Break too shallow to be convincing.");
            return new StrategySignal(input.Symbol.ToUpperInvariant(), TradeAction.Buy,
                policy.MaxPositionNotional, confidence, Name,
                "Broke above the opening range.", input.DataTimestampUtc);
        }

        if (input.LastPrice < low)
            return new StrategySignal(input.Symbol.ToUpperInvariant(), TradeAction.Sell,
                policy.MaxPositionNotional, 0.70m, Name,
                "Broke below the opening range.", input.DataTimestampUtc);

        return StrategyHelpers.Hold(input, Name, "Inside the opening range.");
    }
}
