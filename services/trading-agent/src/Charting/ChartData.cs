using ClaudeTradingAgent.TechnicalAnalysis;

namespace ClaudeTradingAgent.Charting;

public sealed record ChartCandle(DateTimeOffset TimeUtc, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

public sealed record ChartPoint(DateTimeOffset TimeUtc, decimal Value);

/// <summary>A line drawn over the candles: VWAP, an EMA, an SMA.</summary>
public sealed record ChartSeries(string Name, IReadOnlyList<ChartPoint> Points);

public sealed record ChartLevel(string Kind, decimal Price, int Touches, string Source);

public enum ChartMarkerKind { SwingHigh, SwingLow, Entry, Stop, Target, Exit }

public sealed record ChartMarker(ChartMarkerKind Kind, DateTimeOffset TimeUtc, decimal Price, string? Label = null);

/// <summary>
/// Everything a UI needs to draw one chart: candles, volume, overlays, swing
/// points, support and resistance, trade markers, and the analysis behind
/// them. A data contract — no rendering.
/// </summary>
public sealed record ChartData(
    string Symbol,
    string Timeframe,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<ChartCandle> Candles,
    IReadOnlyList<ChartPoint> Volume,
    IReadOnlyList<ChartSeries> Overlays,
    IReadOnlyList<ChartMarker> SwingHighs,
    IReadOnlyList<ChartMarker> SwingLows,
    IReadOnlyList<ChartLevel> Support,
    IReadOnlyList<ChartLevel> Resistance,
    IReadOnlyList<ChartMarker> TradeMarkers,
    TimeframeAnalysis Analysis);
