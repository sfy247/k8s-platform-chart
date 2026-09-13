using ClaudeTradingAgent.Indicators;
using ClaudeTradingAgent.MarketData;

namespace ClaudeTradingAgent.TechnicalAnalysis;

/// <summary>Everything phase 1 knows about one symbol on one timeframe at one moment.</summary>
public sealed record TimeframeAnalysis(
    string Symbol,
    Timeframe Timeframe,
    DateTimeOffset AsOfUtc,
    int CandleCount,
    TrendAssessment Trend,
    MarketStructure Structure,
    IReadOnlyList<SwingPoint> Swings,
    IReadOnlyList<PriceLevel> Levels,
    BreakoutSignal? Breakout,
    decimal? Atr,
    decimal? Vwap,
    decimal? RelativeVolume);

/// <summary>
/// Runs the full phase 1 analysis over one timeframe.
///
/// Only candles that had CLOSED at <c>asOfUtc</c> are used. The breakout is
/// judged against resistance built from the candles BEFORE the latest one,
/// so the candle being tested never defines its own level.
/// </summary>
public sealed class TechnicalAnalyzer(TechnicalAnalysisSettings? settings = null)
{
    private readonly TechnicalAnalysisSettings _s = settings ?? TechnicalAnalysisSettings.Default;

    public TimeframeAnalysis Analyze(IReadOnlyList<Candle> candles, Timeframe timeframe, DateTimeOffset asOfUtc)
    {
        var closed = candles.Where(c => c.CloseTimeUtc <= asOfUtc).OrderBy(c => c.TimestampUtc).ToList();
        var symbol = closed.FirstOrDefault()?.Symbol ?? candles.FirstOrDefault()?.Symbol ?? "";

        var errors = CandleSeries.Validate(closed);
        if (errors.Count > 0)
            throw new ArgumentException($"Candle series is not analysable: {string.Join(" ", errors)}", nameof(candles));

        if (closed.Count == 0)
        {
            var empty = new MarketStructure(StructureState.Undetermined, null, null, null, null, ["No closed candles."]);
            return new TimeframeAnalysis(symbol, timeframe, asOfUtc, 0, TrendAssessment.Undetermined("No closed candles."), empty, [], [], null, null, null, null);
        }

        var swings = new SwingPointDetector(_s.SwingLeftBars, _s.SwingRightBars).Detect(closed);
        var structure = new MarketStructureDetector(_s.EqualSwingTolerancePercent).Classify(swings);
        var vwap = SessionVwap.Calculate(closed)[^1];
        var levels = new SupportResistanceDetector(_s).Detect(closed, swings, vwap);

        BreakoutSignal? breakout = null;
        if (closed.Count >= 2)
        {
            var prior = closed.Take(closed.Count - 1).ToList();
            var priorSwings = swings.Where(s => s.ConfirmedAtIndex <= prior.Count - 1).ToList();
            var resistance = new SupportResistanceDetector(_s).Detect(prior, priorSwings)
                .Where(l => l.Kind == LevelKind.Resistance)
                .OrderBy(l => l.Price)
                .FirstOrDefault();
            if (resistance is not null)
                breakout = new BreakoutDetector(_s).Evaluate(closed, resistance.Price, BreakoutDirection.Up);
        }

        return new TimeframeAnalysis(
            symbol, timeframe, asOfUtc, closed.Count,
            new TrendDetector(_s).Assess(closed),
            structure, swings, levels, breakout,
            AverageTrueRange.Calculate(closed, _s.AtrPeriod)[^1],
            vwap,
            RelativeVolume.Trailing(closed, _s.RelativeVolumeLookback)[^1]);
    }
}

public sealed record MultiTimeframeAnalysis(
    string Symbol,
    DateTimeOffset AsOfUtc,
    IReadOnlyDictionary<Timeframe, TimeframeAnalysis> ByTimeframe);

/// <summary>
/// Prepares 15m (broad trend), 5m (setup structure) and 1m (entry timing)
/// analysis from one series of 1-minute candles. Phase 1 only prepares the
/// three views; deciding what their alignment means is phase 3.
/// </summary>
public sealed class MultiTimeframeAnalyzer(TechnicalAnalysisSettings? settings = null)
{
    private static readonly Timeframe[] Timeframes = [Timeframe.FifteenMinutes, Timeframe.FiveMinutes, Timeframe.OneMinute];
    private readonly TechnicalAnalyzer _analyzer = new(settings);

    public MultiTimeframeAnalysis Analyze(IReadOnlyList<Candle> oneMinute, DateTimeOffset asOfUtc)
    {
        var result = new Dictionary<Timeframe, TimeframeAnalysis>();
        foreach (var timeframe in Timeframes)
            result[timeframe] = _analyzer.Analyze(CandleAggregator.Aggregate(oneMinute, timeframe, asOfUtc), timeframe, asOfUtc);
        return new MultiTimeframeAnalysis(oneMinute.FirstOrDefault()?.Symbol ?? "", asOfUtc, result);
    }
}
