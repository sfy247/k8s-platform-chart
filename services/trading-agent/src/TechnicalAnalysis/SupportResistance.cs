using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.TechnicalAnalysis;

public enum LevelKind { Support, Resistance }

public sealed record PriceLevel(
    LevelKind Kind,
    decimal Price,
    int Touches,
    DateTimeOffset FirstTouchUtc,
    DateTimeOffset LastTouchUtc,
    string Source);

/// <summary>
/// Support and resistance from confirmed swings — explainable, no fitting.
///
/// Swing prices are sorted and grouped: a price joins the current group when
/// it is within <c>LevelTolerancePercent</c> of that group's running mean.
/// Each group becomes one level at its mean price, with one touch per swing.
/// A level at or below the latest close is support; above it, resistance.
/// Session VWAP, when supplied, is added as a single-touch dynamic level.
/// </summary>
public sealed class SupportResistanceDetector(TechnicalAnalysisSettings? settings = null)
{
    private readonly TechnicalAnalysisSettings _s = settings ?? TechnicalAnalysisSettings.Default;

    public IReadOnlyList<PriceLevel> Detect(IReadOnlyList<Candle> candles, IReadOnlyList<SwingPoint> swings, decimal? vwap = null)
    {
        if (candles.Count == 0) return [];
        var close = candles[^1].Close;
        var levels = new List<PriceLevel>();

        var group = new List<SwingPoint>();
        foreach (var swing in swings.OrderBy(s => s.Price).ThenBy(s => s.TimestampUtc))
        {
            if (group.Count > 0)
            {
                var mean = group.Average(g => g.Price);
                if (swing.Price > mean * (1 + _s.LevelTolerancePercent / 100m))
                {
                    levels.Add(ToLevel(group, close));
                    group = [];
                }
            }
            group.Add(swing);
        }
        if (group.Count > 0) levels.Add(ToLevel(group, close));

        if (vwap is { } v)
        {
            var at = candles[^1].CloseTimeUtc;
            levels.Add(new PriceLevel(v <= close ? LevelKind.Support : LevelKind.Resistance, v, 1, at, at, "vwap"));
        }

        // Nearest first on each side.
        return levels.Where(l => l.Kind == LevelKind.Support).OrderByDescending(l => l.Price)
            .Concat(levels.Where(l => l.Kind == LevelKind.Resistance).OrderBy(l => l.Price))
            .ToList();
    }

    private static PriceLevel ToLevel(List<SwingPoint> group, decimal close)
    {
        var price = group.Average(g => g.Price);
        return new PriceLevel(
            price <= close ? LevelKind.Support : LevelKind.Resistance,
            price,
            group.Count,
            group.Min(g => g.TimestampUtc),
            group.Max(g => g.TimestampUtc),
            "swing");
    }
}
