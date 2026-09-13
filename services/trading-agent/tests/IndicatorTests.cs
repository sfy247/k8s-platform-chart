using ClaudeTradingAgent.Indicators;
using ClaudeTradingAgent.MarketData;
using Xunit;

namespace ClaudeTradingAgent.Tests;

/// <summary>Phase 1 indicators, against small fixtures with hand-computed answers.</summary>
public sealed class IndicatorTests
{
    // 2026-09-14 09:30 New York (EDT).
    internal static readonly DateTimeOffset Open = new(2026, 9, 14, 13, 30, 0, TimeSpan.Zero);

    internal static Candle C(int minute, decimal o, decimal h, decimal l, decimal c, long v, int day = 0, Timeframe tf = Timeframe.OneMinute) =>
        new("TEST", tf, Open.AddDays(day).AddMinutes(minute), o, h, l, c, v);

    private static decimal? R(decimal? x) => x is null ? null : Math.Round(x.Value, 6);

    [Fact]
    public void Sma_uses_only_the_trailing_window()
    {
        Assert.Equal(new decimal?[] { null, null, 2, 3, 4 }, MovingAverage.Sma([1, 2, 3, 4, 5], 3));
    }

    [Fact]
    public void Ema_is_seeded_with_the_sma_then_smoothed()
    {
        // period 2: α = 2/3, seed = (2+4)/2 = 3 → 5 → 7 → 7 + 13×2/3
        var ema = MovingAverage.Ema([2, 4, 6, 8, 20], 2).Select(R).ToArray();
        Assert.Equal(new decimal?[] { null, 3m, 5m, 7m, 15.666667m }, ema);
    }

    [Fact]
    public void Ema_is_null_until_the_period_has_data() =>
        Assert.All(MovingAverage.Ema([1, 2], 3), v => Assert.Null(v));

    [Fact]
    public void True_range_includes_gaps_from_the_previous_close()
    {
        var candles = new[]
        {
            C(0, 9, 10, 8, 9, 1), C(1, 10, 11, 9, 10, 1), C(2, 11, 12, 10, 11, 1),
            C(3, 14, 15, 11, 14, 1), C(4, 19.5m, 20, 19, 19.5m, 1),   // gap: |20 − 14| = 6
        };
        Assert.Equal(new decimal[] { 2, 2, 2, 4, 6 }, AverageTrueRange.TrueRange(candles));
        Assert.Equal(new decimal?[] { null, null, 2m, 2.666667m, 3.777778m }, AverageTrueRange.Calculate(candles, 3).Select(R).ToArray());
    }

    [Fact]
    public void Vwap_weights_typical_price_by_volume()
    {
        // typical 10 × 100, then 12 × 300 → 4,600 / 400 = 11.5
        var vwap = SessionVwap.Calculate([C(0, 10, 11, 9, 10, 100), C(1, 12, 13, 11, 12, 300)]);
        Assert.Equal(new decimal?[] { 10m, 11.5m }, vwap);
    }

    [Fact]
    public void Vwap_resets_at_each_new_session()
    {
        var vwap = SessionVwap.Calculate([C(0, 10, 11, 9, 10, 100), C(1, 12, 13, 11, 12, 300), C(0, 20, 21, 19, 20, 50, day: 1)]);
        Assert.Equal(20m, vwap[2]);   // not blended with yesterday's 11.5
    }

    [Fact]
    public void Vwap_is_null_before_any_volume() =>
        Assert.Null(SessionVwap.Calculate([C(0, 10, 11, 9, 10, 0)])[0]);

    [Fact]
    public void Trailing_relative_volume_excludes_the_current_candle()
    {
        var rvol = RelativeVolume.Trailing([C(0, 1, 1, 1, 1, 100), C(1, 1, 1, 1, 1, 100), C(2, 1, 1, 1, 1, 300), C(3, 1, 1, 1, 1, 50)], 2);
        Assert.Equal(new decimal?[] { null, null, 3m, 0.25m }, rvol);
    }

    [Fact]
    public void Time_of_day_relative_volume_compares_like_with_like()
    {
        var rvol = RelativeVolume.ByTimeOfDay(
        [
            C(0, 1, 1, 1, 1, 100, day: 0),
            C(0, 1, 1, 1, 1, 300, day: 1),
            C(0, 1, 1, 1, 1, 400, day: 2),
            C(1, 1, 1, 1, 1, 50, day: 2),     // 09:31 has no earlier session
        ], lookbackSessions: 2);
        Assert.Equal(new decimal?[] { null, 3m, 2m, null }, rvol);
    }

    [Fact]
    public void Spread_percent_is_relative_to_the_midpoint()
    {
        Assert.True(Spread.TryPercent(99.95m, 100.05m, out var pct, out _));
        Assert.Equal(0.1m, pct);
        Assert.False(Spread.TryPercent(100.05m, 99.95m, out _, out var crossed));
        Assert.Contains("crossed", crossed);
        Assert.False(Spread.TryPercent(0m, 100m, out _, out _));
    }

    [Fact]
    public void Aggregation_never_emits_a_candle_that_is_still_forming()
    {
        var oneMinute = Enumerable.Range(0, 7).Select(i => C(i, 10 + i, 11 + i, 9 + i, 10.5m + i, 100)).ToList();

        var at1337 = CandleAggregator.Aggregate(oneMinute, Timeframe.FiveMinutes, Open.AddMinutes(7));
        var bar = Assert.Single(at1337);   // 09:35 bucket closes 09:40, not yet
        Assert.Equal(Open, bar.TimestampUtc);
        Assert.Equal((10m, 15m, 9m, 14.5m, 500L), (bar.Open, bar.High, bar.Low, bar.Close, bar.Volume));
        Assert.Equal(Timeframe.FiveMinutes, bar.Timeframe);
    }

    [Fact]
    public void Indicator_values_never_change_when_later_candles_arrive()
    {
        var candles = Enumerable.Range(0, 40).Select(i => C(i, 100 + i % 7, 101 + i % 7, 99 + i % 5, 100 + i % 6, 100 + i * 3)).ToList();
        var prefix = candles.Take(25).ToList();

        Assert.Equal(MovingAverage.Ema(CandleValues.Closes(prefix), 9), MovingAverage.Ema(CandleValues.Closes(candles), 9).Take(25));
        Assert.Equal(AverageTrueRange.Calculate(prefix, 14), AverageTrueRange.Calculate(candles, 14).Take(25));
        Assert.Equal(SessionVwap.Calculate(prefix), SessionVwap.Calculate(candles).Take(25));
        Assert.Equal(RelativeVolume.Trailing(prefix, 5), RelativeVolume.Trailing(candles, 5).Take(25));
    }

    [Fact]
    public void Candle_series_validation_catches_bad_input()
    {
        Assert.Empty(CandleSeries.Validate([C(0, 10, 11, 9, 10, 1), C(1, 10, 11, 9, 10, 1)]));
        Assert.NotEmpty(CandleSeries.Validate([C(1, 10, 11, 9, 10, 1), C(0, 10, 11, 9, 10, 1)]));   // out of order
        Assert.NotEmpty(CandleSeries.Validate([C(0, 10, 9, 8, 10, 1)]));                            // high below close
    }
}
