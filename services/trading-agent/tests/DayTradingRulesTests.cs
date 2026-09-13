using ClaudeTradingAgent.MarketData;
using ClaudeTradingAgent.RiskManagement;
using Xunit;

namespace ClaudeTradingAgent.Tests;

/// <summary>v3 session management: the schedule, the five states, and when positions must close.</summary>
public sealed class DayTradingRulesTests
{
    // A regular 09:30–16:00 New York session during daylight time.
    private static readonly TradingSession Regular = new(
        new DateOnly(2026, 9, 4),
        new DateTimeOffset(2026, 9, 4, 13, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 4, 20, 0, 0, TimeSpan.Zero));

    private static readonly SessionPolicy V3 = new(new TimeOnly(9, 35), new TimeOnly(15, 30), new TimeOnly(15, 55));
    private static readonly ExitPolicy Exits = new(0.75m, 1.50m, TimeSpan.FromMinutes(90));
    private static SessionSchedule Schedule => SessionWindow.Schedule(Regular, V3);

    [Fact]
    public void On_a_regular_day_the_schedule_is_the_configured_clock_times()
    {
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 13, 35, 0, TimeSpan.Zero), Schedule.EntryStartUtc);   // 09:35 ET
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 19, 30, 0, TimeSpan.Zero), Schedule.EntryCutoffUtc);  // 15:30 ET
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 19, 55, 0, TimeSpan.Zero), Schedule.FlattenUtc);      // 15:55 ET
    }

    [Fact]
    public void On_an_early_close_the_flatten_moves_with_the_close()
    {
        // Day after Thanksgiving: 09:30–13:00 ET (standard time). A fixed
        // 15:55 flatten would land three hours after the market shut.
        var halfDay = new TradingSession(new DateOnly(2026, 11, 27),
            new DateTimeOffset(2026, 11, 27, 14, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero));
        var s = SessionWindow.Schedule(halfDay, V3);

        Assert.Equal(new DateTimeOffset(2026, 11, 27, 17, 30, 0, TimeSpan.Zero), s.EntryCutoffUtc);   // 12:30 ET
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 17, 55, 0, TimeSpan.Zero), s.FlattenUtc);       // 12:55 ET
        Assert.True(s.FlattenUtc < s.CloseUtc);
    }

    [Theory]
    [InlineData(2, SessionState.PreMarketDisabled)]
    [InlineData(5, SessionState.EntryWindow)]
    [InlineData(359, SessionState.EntryWindow)]
    [InlineData(360, SessionState.ManagementOnly)]
    [InlineData(384, SessionState.ManagementOnly)]
    [InlineData(385, SessionState.FlattenWindow)]
    [InlineData(391, SessionState.FlattenWindow)]   // clock skew past the close still tries to get flat
    public void Resolves_the_session_state_from_minutes_after_the_open(int minutes, SessionState expected) =>
        Assert.Equal(expected, SessionWindow.Resolve(true, Schedule, Regular.OpenUtc.AddMinutes(minutes)));

    [Fact]
    public void A_closed_market_is_MARKET_CLOSED_whatever_the_time() =>
        Assert.Equal(SessionState.MarketClosed, SessionWindow.Resolve(false, Schedule, Regular.OpenUtc.AddHours(2)));

    [Fact]
    public void A_session_too_short_for_the_windows_has_no_entry_window()
    {
        var tiny = Regular with { CloseUtc = Regular.OpenUtc.AddMinutes(30) };
        var s = SessionWindow.Schedule(tiny, V3);
        for (var m = 0; m < 30; m++)
            Assert.NotEqual(SessionState.EntryWindow, SessionWindow.Resolve(true, s, tiny.OpenUtc.AddMinutes(m)));
    }

    [Fact]
    public void Labels_use_the_v3_state_names()
    {
        Assert.Equal("ENTRY_WINDOW", SessionWindow.Label(SessionState.EntryWindow));
        Assert.Equal("MANAGEMENT_ONLY", SessionWindow.Label(SessionState.ManagementOnly));
        Assert.Equal("FLATTEN_WINDOW", SessionWindow.Label(SessionState.FlattenWindow));
        Assert.Equal("PRE_MARKET_DISABLED", SessionWindow.Label(SessionState.PreMarketDisabled));
        Assert.Equal("MARKET_CLOSED", SessionWindow.Label(SessionState.MarketClosed));
    }

    [Fact]
    public void Session_policy_validation()
    {
        Assert.Empty(V3.Validate());
        Assert.NotEmpty((V3 with { FlattenEt = new TimeOnly(15, 20) }).Validate());      // flatten before cutoff
        Assert.NotEmpty((V3 with { FlattenEt = new TimeOnly(16, 0) }).Validate());       // at the close
        Assert.NotEmpty((V3 with { EntryStartEt = new TimeOnly(9, 0) }).Validate());     // before the open
        Assert.NotEmpty((V3 with { EntryCutoffEt = new TimeOnly(9, 35) }).Validate());   // empty entry window
    }

    // ── Exits ────────────────────────────────────────────────────────────

    private static DateTimeOffset MidSession => Regular.OpenUtc.AddHours(2);

    private static PositionSnapshot Position(decimal? pnlFraction, DateTimeOffset? openedAt = null) =>
        new("AAPL", 0.05m, 10m, 200m, 200m, 10m * (pnlFraction ?? 0m), pnlFraction, openedAt);

    [Fact]
    public void Flattens_at_1555_even_a_winning_position()
    {
        var d = ExitManager.Evaluate(Position(0.005m), Exits, Schedule, Schedule.FlattenUtc);
        Assert.True(d.ShouldExit);
        Assert.Equal(ExitReason.SessionClose, d.Reason);
        Assert.False(ExitManager.Evaluate(Position(0.005m), Exits, Schedule, Schedule.FlattenUtc.AddMinutes(-1)).ShouldExit);
    }

    [Fact] public void Closes_at_the_stop() => Assert.Equal(ExitReason.StopLoss, ExitManager.Evaluate(Position(-0.008m), Exits, Schedule, MidSession).Reason);
    [Fact] public void Closes_at_the_target() => Assert.Equal(ExitReason.TakeProfit, ExitManager.Evaluate(Position(0.016m), Exits, Schedule, MidSession).Reason);
    [Fact] public void Holds_between_stop_and_target() => Assert.False(ExitManager.Evaluate(Position(0.002m), Exits, Schedule, MidSession).ShouldExit);
    [Fact] public void Closes_a_stale_position_after_max_hold() => Assert.Equal(ExitReason.MaxHoldTime, ExitManager.Evaluate(Position(0.001m, MidSession.AddMinutes(-91)), Exits, Schedule, MidSession).Reason);
    [Fact] public void Invents_no_max_hold_without_an_entry_time() => Assert.False(ExitManager.Evaluate(Position(0.001m), Exits, Schedule, MidSession).ShouldExit);
    [Fact] public void Suspends_the_stop_when_pnl_is_unknown() => Assert.False(ExitManager.Evaluate(Position(null), Exits, Schedule, MidSession).ShouldExit);
    [Fact] public void Still_flattens_when_pnl_is_unknown() => Assert.Equal(ExitReason.SessionClose, ExitManager.Evaluate(Position(null), Exits, Schedule, Schedule.FlattenUtc).Reason);
    [Fact] public void Still_flattens_when_quantity_is_unknown() => Assert.True(ExitManager.Evaluate(Position(0m) with { Quantity = null }, Exits, Schedule, Schedule.FlattenUtc).ShouldExit);
    [Fact] public void Ignores_a_position_with_no_value() => Assert.False(ExitManager.Evaluate(Position(-0.05m) with { MarketValue = 0m }, Exits, Schedule, MidSession).ShouldExit);
}

/// <summary>A refused quote is refused for a reason; the reason separates a thin feed from a stale connection.</summary>
public sealed class MarketDataValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 15, 0, 0, TimeSpan.Zero);
    private static QuoteSnapshot Quote(decimal bid, decimal ask, int ageSeconds = 0) => new("AAPL", bid, ask, Now.AddSeconds(-ageSeconds));
    private static MarketDataRejection? Reason(QuoteSnapshot q)
    {
        try { MarketDataValidator.ValidateQuote(q, Now, TimeSpan.FromSeconds(10), 25m); return null; }
        catch (MarketDataException ex) { return ex.Reason; }
    }

    [Fact] public void Accepts_a_tight_fresh_quote() => Assert.Null(Reason(Quote(200.00m, 200.02m)));
    [Fact] public void Labels_a_wide_spread() => Assert.Equal(MarketDataRejection.WideSpread, Reason(Quote(200m, 201m)));
    [Fact] public void Labels_a_stale_quote() => Assert.Equal(MarketDataRejection.Stale, Reason(Quote(200m, 200.02m, ageSeconds: 11)));
    [Fact] public void Labels_a_crossed_quote() => Assert.Equal(MarketDataRejection.CrossedQuote, Reason(Quote(201m, 200m)));
    [Fact] public void Labels_a_non_positive_price() => Assert.Equal(MarketDataRejection.NonPositivePrice, Reason(Quote(0m, 200m)));
    [Fact] public void Is_still_an_InvalidOperationException_for_fail_closed_handlers() =>
        Assert.IsAssignableFrom<InvalidOperationException>(new MarketDataException(MarketDataRejection.Stale, "x"));
}
