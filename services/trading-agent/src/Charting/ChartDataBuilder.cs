using ClaudeTradingAgent.Indicators;
using ClaudeTradingAgent.MarketData;
using ClaudeTradingAgent.TechnicalAnalysis;

namespace ClaudeTradingAgent.Charting;

/// <summary>Where 1-minute candles come from. The builder does not know or care that it is Alpaca.</summary>
public interface ICandleSource
{
    Task<IReadOnlyList<Candle>> GetOneMinuteCandlesAsync(string symbol, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds chart-ready data for one symbol and timeframe.
///
/// Indicators are computed over a warm-up window before <c>startUtc</c> and
/// then trimmed to the requested range, so the first visible EMA50 point is a
/// real EMA50 rather than a blank or a value seeded from the chart's edge.
/// Everything is computed as of <c>endUtc</c>: no candle, swing or level that
/// had not formed by then appears.
/// </summary>
public sealed class ChartDataBuilder(ICandleSource source, TechnicalAnalysisSettings? settings = null)
{
    private readonly TechnicalAnalysisSettings _s = settings ?? TechnicalAnalysisSettings.Default;

    public async Task<ChartData> BuildAsync(
        string symbol, Timeframe timeframe, DateTimeOffset startUtc, DateTimeOffset endUtc,
        IReadOnlyList<ChartMarker>? tradeMarkers = null, CancellationToken cancellationToken = default)
    {
        if (endUtc <= startUtc) throw new ArgumentException("End must be after start.");
        var oneMinute = await source.GetOneMinuteCandlesAsync(symbol, startUtc - WarmUp(timeframe, _s), endUtc, cancellationToken);
        return Build(symbol, timeframe, oneMinute, startUtc, endUtc, tradeMarkers, _s);
    }

    /// <summary>Calendar time to fetch before the range: enough regular-session candles for the longest indicator, plus a weekend.</summary>
    public static TimeSpan WarmUp(Timeframe timeframe, TechnicalAnalysisSettings settings)
    {
        var candlesNeeded = Math.Max(settings.EmaTrendPeriod, Math.Max(settings.AtrPeriod, settings.SmaPeriod)) + settings.SlopeLookback + settings.MomentumLookback;
        var sessions = Math.Ceiling(candlesNeeded * (int)timeframe / 390.0);   // 390 minutes in a regular session
        return TimeSpan.FromDays(sessions + 3);
    }

    public static ChartData Build(
        string symbol, Timeframe timeframe, IReadOnlyList<Candle> oneMinute,
        DateTimeOffset startUtc, DateTimeOffset endUtc,
        IReadOnlyList<ChartMarker>? tradeMarkers = null, TechnicalAnalysisSettings? settings = null)
    {
        var s = settings ?? TechnicalAnalysisSettings.Default;
        var candles = CandleAggregator.Aggregate(oneMinute.OrderBy(c => c.TimestampUtc).ToList(), timeframe, endUtc);
        var analysis = new TechnicalAnalyzer(s).Analyze(candles, timeframe, endUtc);
        var closes = CandleValues.Closes(candles);

        bool InRange(DateTimeOffset t) => t >= startUtc && t <= endUtc;

        IReadOnlyList<ChartPoint> Points(IReadOnlyList<decimal?> values) =>
            candles.Select((c, i) => (c, v: values[i]))
                .Where(x => x.v is not null && InRange(x.c.TimestampUtc))
                .Select(x => new ChartPoint(x.c.TimestampUtc, x.v!.Value))
                .ToList();

        var overlays = new List<ChartSeries>
        {
            new("VWAP", Points(SessionVwap.Calculate(candles))),
            new($"EMA{s.EmaFastPeriod}", Points(MovingAverage.Ema(closes, s.EmaFastPeriod))),
            new($"EMA{s.EmaSlowPeriod}", Points(MovingAverage.Ema(closes, s.EmaSlowPeriod))),
            new($"EMA{s.EmaTrendPeriod}", Points(MovingAverage.Ema(closes, s.EmaTrendPeriod))),
            new($"SMA{s.SmaPeriod}", Points(MovingAverage.Sma(closes, s.SmaPeriod))),
        };

        var visible = candles.Where(c => InRange(c.TimestampUtc)).ToList();
        ChartMarker Swing(SwingPoint p) => new(p.Kind == SwingKind.High ? ChartMarkerKind.SwingHigh : ChartMarkerKind.SwingLow, p.TimestampUtc, p.Price);

        return new ChartData(
            symbol.ToUpperInvariant(), timeframe.Label(), startUtc, endUtc,
            visible.Select(c => new ChartCandle(c.TimestampUtc, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList(),
            visible.Select(c => new ChartPoint(c.TimestampUtc, c.Volume)).ToList(),
            overlays,
            analysis.Swings.Where(p => p.Kind == SwingKind.High && InRange(p.TimestampUtc)).Select(Swing).ToList(),
            analysis.Swings.Where(p => p.Kind == SwingKind.Low && InRange(p.TimestampUtc)).Select(Swing).ToList(),
            analysis.Levels.Where(l => l.Kind == LevelKind.Support).Select(l => new ChartLevel("SUPPORT", l.Price, l.Touches, l.Source)).ToList(),
            analysis.Levels.Where(l => l.Kind == LevelKind.Resistance).Select(l => new ChartLevel("RESISTANCE", l.Price, l.Touches, l.Source)).ToList(),
            (tradeMarkers ?? []).Where(m => InRange(m.TimeUtc) && m.Kind is ChartMarkerKind.Entry or ChartMarkerKind.Stop or ChartMarkerKind.Target or ChartMarkerKind.Exit).ToList(),
            analysis);
    }
}
