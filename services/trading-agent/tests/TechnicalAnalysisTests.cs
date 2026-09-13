using ClaudeTradingAgent.Charting;
using ClaudeTradingAgent.MarketData;
using ClaudeTradingAgent.TechnicalAnalysis;
using Xunit;
using static ClaudeTradingAgent.Tests.IndicatorTests;

namespace ClaudeTradingAgent.Tests;

/// <summary>Phase 1 swings, structure, trend, levels, breakouts and chart data.</summary>
public sealed class TechnicalAnalysisTests
{
    /// <summary>Highs 10 11 15 12 11 13 16 14 13; lows are one below. Swing highs at 2 and 6, a swing low at 4.</summary>
    private static List<Candle> SwingFixture() =>
        new decimal[] { 10, 11, 15, 12, 11, 13, 16, 14, 13 }
            .Select((h, i) => C(i, h - 0.5m, h, h - 1, h - 0.5m, 100)).ToList();

    /// <summary>60 one-minute candles drifting up with a 4-candle zigzag: clean higher highs and higher lows.</summary>
    private static List<Candle> Trend(bool up) =>
        Enumerable.Range(0, 60).Select(i =>
        {
            var zig = (i % 4) switch { 1 => 0.3m, 3 => -0.3m, _ => 0m };
            var close = up ? 100m + 0.2m * i + zig : 200m - 0.2m * i - zig;
            return C(i, close, close + 0.1m, close - 0.1m, close, 1000);
        }).ToList();

    private static SwingPoint S(SwingKind kind, int index, decimal price) =>
        new(kind, index, Open.AddMinutes(index), price, index + 2, Open.AddMinutes(index + 3));

    // ── Swings ───────────────────────────────────────────────────────────

    [Fact]
    public void Detects_swing_highs_and_lows()
    {
        var swings = new SwingPointDetector(2, 2).Detect(SwingFixture());
        Assert.Equal([(SwingKind.High, 2, 15m), (SwingKind.Low, 4, 10m), (SwingKind.High, 6, 16m)],
            swings.Select(s => (s.Kind, s.Index, s.Price)).ToList());
    }

    [Fact]
    public void A_swing_does_not_exist_until_its_confirming_candles_have_closed()
    {
        var fixture = SwingFixture();
        Assert.DoesNotContain(new SwingPointDetector(2, 2).Detect(fixture.Take(8).ToList()), s => s.Index == 6);   // one right bar short

        var confirmed = new SwingPointDetector(2, 2).Detect(fixture).Single(s => s.Index == 6);
        Assert.Equal(8, confirmed.ConfirmedAtIndex);
        Assert.Equal(fixture[8].CloseTimeUtc, confirmed.ConfirmedAtUtc);
    }

    // ── Structure ────────────────────────────────────────────────────────

    [Fact]
    public void Higher_highs_and_higher_lows_are_bullish() =>
        Assert.Equal(StructureState.Bullish, new MarketStructureDetector().Classify([S(SwingKind.High, 1, 10), S(SwingKind.Low, 2, 8), S(SwingKind.High, 3, 12), S(SwingKind.Low, 4, 9)]).State);

    [Fact]
    public void Lower_highs_and_lower_lows_are_bearish() =>
        Assert.Equal(StructureState.Bearish, new MarketStructureDetector().Classify([S(SwingKind.High, 1, 12), S(SwingKind.Low, 2, 9), S(SwingKind.High, 3, 10), S(SwingKind.Low, 4, 8)]).State);

    [Fact]
    public void Mixed_swings_are_a_range() =>
        Assert.Equal(StructureState.Range, new MarketStructureDetector().Classify([S(SwingKind.High, 1, 10), S(SwingKind.Low, 2, 9), S(SwingKind.High, 3, 12), S(SwingKind.Low, 4, 8)]).State);

    [Fact]
    public void Too_few_swings_are_undetermined() =>
        Assert.Equal("UNDETERMINED", new MarketStructureDetector().Classify([S(SwingKind.High, 1, 10), S(SwingKind.Low, 2, 9), S(SwingKind.Low, 4, 8)]).Label);

    // ── Trend ────────────────────────────────────────────────────────────

    [Fact]
    public void A_clean_uptrend_scores_every_bullish_point()
    {
        var t = new TrendDetector().Assess(Trend(up: true));
        Assert.Equal((TrendDirection.Bullish, TrendStrength.Strong, 100, 100), (t.Direction, t.Strength, t.Score, t.ConfidenceScore));
        Assert.Contains(t.Evidence, e => e.StartsWith("Price above VWAP"));
        Assert.Contains(t.Evidence, e => e.StartsWith("Higher highs and higher lows"));
    }

    [Fact]
    public void A_clean_downtrend_scores_every_bearish_point()
    {
        var t = new TrendDetector().Assess(Trend(up: false));
        Assert.Equal((TrendDirection.Bearish, -100), (t.Direction, t.Score));
    }

    [Fact]
    public void Trend_scoring_is_deterministic()
    {
        var candles = Trend(up: true);
        var a = new TrendDetector().Assess(candles);
        var b = new TrendDetector().Assess(candles.ToList());
        Assert.Equal(a.Score, b.Score);
        Assert.Equal(a.Evidence, b.Evidence);
    }

    [Fact]
    public void Too_little_data_is_undetermined() =>
        Assert.Equal(TrendDirection.Undetermined, new TrendDetector().Assess(Trend(up: true).Take(10).ToList()).Direction);

    // ── Support / resistance ─────────────────────────────────────────────

    [Fact]
    public void Nearby_swings_cluster_into_levels_either_side_of_price()
    {
        var levels = new SupportResistanceDetector().Detect(
            [C(0, 105, 106, 104, 105, 100)],
            [S(SwingKind.Low, 1, 100.00m), S(SwingKind.Low, 5, 100.08m), S(SwingKind.High, 3, 110.00m), S(SwingKind.High, 7, 110.05m), S(SwingKind.High, 9, 120m)]);

        var support = Assert.Single(levels, l => l.Kind == LevelKind.Support);
        Assert.Equal((100.04m, 2), (support.Price, support.Touches));
        Assert.Equal([(110.025m, 2), (120m, 1)], levels.Where(l => l.Kind == LevelKind.Resistance).Select(l => (l.Price, l.Touches)).ToList());
    }

    // ── Breakouts ────────────────────────────────────────────────────────

    private static readonly TechnicalAnalysisSettings BreakoutSettings = new() { RelativeVolumeLookback = 3, BreakoutMinRelativeVolume = 1.5m, BreakoutBufferPercent = 0.05m };

    private static List<Candle> Breakout(decimal high, decimal close, long volume) =>
        [C(0, 99.5m, 99.8m, 99.2m, 99.5m, 100), C(1, 99.5m, 99.8m, 99.2m, 99.5m, 100), C(2, 99.5m, 99.8m, 99.2m, 99.5m, 100),
         C(3, 99.8m, high, 99.7m, close, volume)];

    [Fact]
    public void A_close_beyond_the_level_on_volume_is_confirmed()
    {
        var b = new BreakoutDetector(BreakoutSettings).Evaluate(Breakout(100.6m, 100.4m, 300), 100m, BreakoutDirection.Up);
        Assert.Equal(BreakoutStatus.Confirmed, b.Status);
        Assert.Equal(3m, b.RelativeVolume);
    }

    [Fact]
    public void A_wick_through_the_level_is_not_a_breakout() =>
        Assert.Equal(BreakoutStatus.WickOnly, new BreakoutDetector(BreakoutSettings).Evaluate(Breakout(100.6m, 99.9m, 300), 100m, BreakoutDirection.Up).Status);

    [Fact]
    public void A_breakout_without_volume_is_not_confirmed() =>
        Assert.Equal(BreakoutStatus.NeedsVolume, new BreakoutDetector(BreakoutSettings).Evaluate(Breakout(100.6m, 100.4m, 120), 100m, BreakoutDirection.Up).Status);

    [Fact]
    public void A_wick_counts_only_when_configured_to() =>
        Assert.Equal(BreakoutStatus.Confirmed,
            new BreakoutDetector(BreakoutSettings with { BreakoutRequireClose = false }).Evaluate(Breakout(100.6m, 99.9m, 300), 100m, BreakoutDirection.Up).Status);

    // ── Analysis, charts, timeframes ─────────────────────────────────────

    [Fact]
    public void The_analyzer_ignores_candles_that_had_not_closed()
    {
        var candles = Trend(up: true);
        var a = new TechnicalAnalyzer().Analyze(candles, Timeframe.OneMinute, candles[29].CloseTimeUtc);
        Assert.Equal(30, a.CandleCount);
        Assert.All(a.Swings, s => Assert.True(s.ConfirmedAtUtc <= candles[29].CloseTimeUtc));
    }

    [Fact]
    public void Chart_data_carries_candles_overlays_swings_and_trade_markers()
    {
        var candles = Trend(up: true);
        var end = candles[^1].CloseTimeUtc;
        var chart = ChartDataBuilder.Build("TEST", Timeframe.OneMinute, candles, Open, end,
        [
            new ChartMarker(ChartMarkerKind.Entry, Open.AddMinutes(10), 102m, "entry"),
            new ChartMarker(ChartMarkerKind.Stop, Open.AddMinutes(10), 101m, "stop"),
            new ChartMarker(ChartMarkerKind.Exit, end.AddHours(1), 110m, "outside the range"),
            new ChartMarker(ChartMarkerKind.SwingHigh, Open.AddMinutes(5), 1m, "not a trade marker"),
        ]);

        Assert.Equal(60, chart.Candles.Count);
        Assert.Equal(60, chart.Volume.Count);
        Assert.Equal(["VWAP", "EMA9", "EMA20", "EMA50", "SMA20"], chart.Overlays.Select(o => o.Name).ToList());
        Assert.Equal(11, chart.Overlays.Single(o => o.Name == "EMA50").Points.Count);   // indices 49..59
        Assert.NotEmpty(chart.SwingHighs);
        Assert.NotEmpty(chart.SwingLows);
        Assert.Equal([ChartMarkerKind.Entry, ChartMarkerKind.Stop], chart.TradeMarkers.Select(m => m.Kind).ToList());
        Assert.Equal(TrendDirection.Bullish, chart.Analysis.Trend.Direction);
    }

    [Fact]
    public void Five_minute_charts_are_built_from_one_minute_candles()
    {
        var candles = Trend(up: true);
        var chart = ChartDataBuilder.Build("TEST", Timeframe.FiveMinutes, candles, Open, candles[^1].CloseTimeUtc);
        Assert.Equal(12, chart.Candles.Count);
        Assert.Equal("5m", chart.Timeframe);
    }

    [Fact]
    public void Multi_timeframe_analysis_prepares_15m_5m_and_1m()
    {
        var candles = Trend(up: true);
        var mtf = new MultiTimeframeAnalyzer().Analyze(candles, candles[^1].CloseTimeUtc);
        Assert.Equal(4, mtf.ByTimeframe[Timeframe.FifteenMinutes].CandleCount);
        Assert.Equal(12, mtf.ByTimeframe[Timeframe.FiveMinutes].CandleCount);
        Assert.Equal(60, mtf.ByTimeframe[Timeframe.OneMinute].CandleCount);
    }
}
