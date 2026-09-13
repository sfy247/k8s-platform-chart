using ClaudeTradingAgent.Indicators;
using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.TechnicalAnalysis;

public enum BreakoutDirection { Up, Down }

public enum BreakoutStatus { None, WickOnly, NeedsVolume, SpreadTooWide, Confirmed }

public sealed record BreakoutSignal(
    BreakoutDirection Direction,
    BreakoutStatus Status,
    decimal Level,
    decimal TriggerPrice,
    decimal Close,
    decimal? RelativeVolume,
    IReadOnlyList<string> Evidence)
{
    public bool IsConfirmed => Status == BreakoutStatus.Confirmed;
}

/// <summary>
/// Judges whether the latest candle broke a level.
///
/// Trigger = level × (1 ± BreakoutBufferPercent). The breakout is CONFIRMED
/// only when the candle closes beyond the trigger (or, if BreakoutRequireClose
/// is false, trades beyond it), trailing relative volume is at least
/// BreakoutMinRelativeVolume, and — when a spread limit and spread are given —
/// the spread is within it. A wick through the trigger that closes back
/// inside is WICK_ONLY, never a breakout.
/// </summary>
public sealed class BreakoutDetector(TechnicalAnalysisSettings? settings = null)
{
    private readonly TechnicalAnalysisSettings _s = settings ?? TechnicalAnalysisSettings.Default;

    public BreakoutSignal Evaluate(IReadOnlyList<Candle> candles, decimal level, BreakoutDirection direction, decimal? spreadPercent = null)
    {
        if (candles.Count == 0) throw new ArgumentException("At least one candle is required.", nameof(candles));
        var c = candles[^1];
        var up = direction == BreakoutDirection.Up;
        var trigger = level * (1 + (up ? 1 : -1) * _s.BreakoutBufferPercent / 100m);
        var rvol = RelativeVolume.Trailing(candles, _s.RelativeVolumeLookback)[^1];
        var evidence = new List<string>();

        var closedBeyond = up ? c.Close > trigger : c.Close < trigger;
        var tradedBeyond = up ? c.High > trigger : c.Low < trigger;
        var side = up ? "above" : "below";

        if (!closedBeyond && !(tradedBeyond && !_s.BreakoutRequireClose))
        {
            if (tradedBeyond)
            {
                evidence.Add($"Wick {side} {trigger:0.####} but closed at {c.Close:0.####}");
                return new BreakoutSignal(direction, BreakoutStatus.WickOnly, level, trigger, c.Close, rvol, evidence);
            }
            evidence.Add($"No trade {side} {trigger:0.####}");
            return new BreakoutSignal(direction, BreakoutStatus.None, level, trigger, c.Close, rvol, evidence);
        }

        evidence.Add(closedBeyond ? $"Closed {side} {trigger:0.####} at {c.Close:0.####}" : $"Traded {side} {trigger:0.####} (close not required)");

        if (_s.BreakoutMaxSpreadPercent is { } maxSpread && spreadPercent is { } spread && spread > maxSpread)
        {
            evidence.Add($"Spread {spread:0.###}% exceeds {maxSpread:0.###}%");
            return new BreakoutSignal(direction, BreakoutStatus.SpreadTooWide, level, trigger, c.Close, rvol, evidence);
        }

        if (rvol is null || rvol < _s.BreakoutMinRelativeVolume)
        {
            evidence.Add(rvol is null ? "Relative volume unavailable" : $"Relative volume {rvol:0.##}x below {_s.BreakoutMinRelativeVolume:0.##}x");
            return new BreakoutSignal(direction, BreakoutStatus.NeedsVolume, level, trigger, c.Close, rvol, evidence);
        }

        evidence.Add($"Relative volume {rvol:0.##}x");
        return new BreakoutSignal(direction, BreakoutStatus.Confirmed, level, trigger, c.Close, rvol, evidence);
    }
}
