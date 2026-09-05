using ClaudeTradingAgent.MarketData;
using ClaudeTradingAgent.RiskManagement;
using Xunit;

namespace ClaudeTradingAgent.Tests;

/// <summary>
/// The rules that make this a day-trading system: where in the session it may
/// enter, and when a position must be closed.
/// </summary>
public sealed class DayTradingRulesTests
{
    // A normal 09:30–16:00 New York session, in UTC during eastern daylight time.
    private static readonly TradingSession Session = new(
        new DateOnly(2026, 9, 4),
        new DateTimeOffset(2026, 9, 4, 13, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 4, 20, 0, 0, TimeSpan.Zero));

    private static readonly SessionPolicy Windows = new(
        NoEntryAfterOpen: TimeSpan.FromMinutes(5),
        NoEntryBeforeClose: TimeSpan.FromMinutes(30),
        FlattenBeforeClose: TimeSpan.FromMinutes(15));

    private static readonly ExitPolicy Exits = new(
        StopLossPercent: 0.75m,
        TakeProfitPercent: 1.50m,
        MaxHoldTime: TimeSpan.FromMinutes(90));

    // ── Entry window ─────────────────────────────────────────────────────

    [Fact]
    public void Refuses_entries_before_the_session_opens()
    {
        var window = SessionWindow.EvaluateEntry(Session, Windows, Session.OpenUtc.AddMinutes(-1));
        Assert.False(window.IsOpen);
        Assert.Equal("SESSION_NOT_STARTED", window.Code);
    }

    [Fact]
    public void Refuses_entries_during_the_opening_minutes()
    {
        var window = SessionWindow.EvaluateEntry(Session, Windows, Session.OpenUtc.AddMinutes(2));
        Assert.False(window.IsOpen);
        Assert.Equal("OPENING_AUCTION", window.Code);
    }

    [Fact]
    public void Allows_entries_through_the_middle_of_the_session()
    {
        Assert.True(SessionWindow.EvaluateEntry(Session, Windows, Session.OpenUtc.AddMinutes(6)).IsOpen);
        Assert.True(SessionWindow.EvaluateEntry(Session, Windows, Session.CloseUtc.AddMinutes(-31)).IsOpen);
    }

    [Fact]
    public void Refuses_entries_inside_the_cutoff_before_the_close()
    {
        var window = SessionWindow.EvaluateEntry(Session, Windows, Session.CloseUtc.AddMinutes(-20));
        Assert.False(window.IsOpen);
        Assert.Equal("ENTRY_CUTOFF", window.Code);
    }

    [Fact]
    public void Refuses_entries_after_the_session_closes()
    {
        var window = SessionWindow.EvaluateEntry(Session, Windows, Session.CloseUtc.AddMinutes(1));
        Assert.False(window.IsOpen);
        Assert.Equal("SESSION_ENDED", window.Code);
    }

    [Fact]
    public void The_entry_cutoff_always_precedes_the_flatten_deadline()
    {
        // Otherwise the agent opens positions it is about to be forced to
        // close, paying the spread twice for no thesis.
        var justInsideFlatten = Session.CloseUtc.AddMinutes(-14);
        Assert.True(SessionWindow.IsFlattenTime(Session, Windows, justInsideFlatten));
        Assert.False(SessionWindow.EvaluateEntry(Session, Windows, justInsideFlatten).IsOpen);
    }

    [Fact]
    public void Flatten_time_is_measured_against_an_early_close()
    {
        // A half day closes at 13:00 New York. A deadline hardcoded to 16:00
        // would try to flatten three hours after the market had gone home.
        var halfDay = Session with { CloseUtc = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero) };
        Assert.True(SessionWindow.IsFlattenTime(halfDay, Windows, halfDay.CloseUtc.AddMinutes(-10)));
        Assert.False(SessionWindow.IsFlattenTime(halfDay, Windows, halfDay.CloseUtc.AddMinutes(-20)));
    }

    [Fact]
    public void A_session_policy_that_would_strand_a_position_is_rejected()
    {
        var backwards = new SessionPolicy(
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
        Assert.NotEmpty(backwards.Validate());
        Assert.Empty(Windows.Validate());
    }

    // ── Exits ────────────────────────────────────────────────────────────

    [Fact]
    public void Closes_every_position_at_the_flatten_deadline_even_a_winning_one()
    {
        var winner = Position(pnlFraction: 0.005m);
        var decision = ExitManager.Evaluate(winner, Exits, Windows, Session, Session.CloseUtc.AddMinutes(-10));
        Assert.True(decision.ShouldExit);
        Assert.Equal(ExitReason.SessionClose, decision.Reason);
    }

    [Fact]
    public void Closes_a_position_that_has_hit_its_stop()
    {
        var loser = Position(pnlFraction: -0.0080m);
        var decision = ExitManager.Evaluate(loser, Exits, Windows, Session, MidSession);
        Assert.True(decision.ShouldExit);
        Assert.Equal(ExitReason.StopLoss, decision.Reason);
    }

    [Fact]
    public void Closes_a_position_that_has_reached_its_target()
    {
        var winner = Position(pnlFraction: 0.0160m);
        var decision = ExitManager.Evaluate(winner, Exits, Windows, Session, MidSession);
        Assert.True(decision.ShouldExit);
        Assert.Equal(ExitReason.TakeProfit, decision.Reason);
    }

    [Fact]
    public void Holds_a_position_between_the_stop_and_the_target()
    {
        var decision = ExitManager.Evaluate(Position(pnlFraction: 0.002m), Exits, Windows, Session, MidSession);
        Assert.False(decision.ShouldExit);
        Assert.Equal(ExitReason.None, decision.Reason);
    }

    [Fact]
    public void Closes_a_position_that_has_gone_nowhere_for_too_long()
    {
        var stale = Position(pnlFraction: 0.001m, openedAt: MidSession.AddMinutes(-91));
        var decision = ExitManager.Evaluate(stale, Exits, Windows, Session, MidSession);
        Assert.True(decision.ShouldExit);
        Assert.Equal(ExitReason.MaxHoldTime, decision.Reason);
    }

    [Fact]
    public void Does_not_invent_a_max_hold_exit_when_the_entry_time_is_unknown()
    {
        // No fill history for the symbol today. The flatten deadline still
        // bounds the hold; a guessed entry time would produce a guessed exit.
        var decision = ExitManager.Evaluate(Position(pnlFraction: 0.001m), Exits, Windows, Session, MidSession);
        Assert.False(decision.ShouldExit);
    }

    [Fact]
    public void Suspends_the_stop_when_the_broker_did_not_report_pnl()
    {
        // A missing number must not be read as zero. Zero would claim the
        // position is flat, which is a live assertion about the trade.
        var unknown = Position(pnlFraction: -0.05m) with { UnrealizedPnlFraction = null };
        Assert.False(unknown.PnlKnown);
        Assert.False(ExitManager.Evaluate(unknown, Exits, Windows, Session, MidSession).ShouldExit);
    }

    [Fact]
    public void Still_flattens_a_position_whose_pnl_is_unknown()
    {
        // The rule that does not need P&L keeps working, so an incomplete
        // broker response cannot strand a position overnight.
        var unknown = Position(pnlFraction: 0m) with { UnrealizedPnlFraction = null };
        var decision = ExitManager.Evaluate(unknown, Exits, Windows, Session, Session.CloseUtc.AddMinutes(-10));
        Assert.True(decision.ShouldExit);
        Assert.Equal(ExitReason.SessionClose, decision.Reason);
    }

    [Fact]
    public void Ignores_a_position_with_no_market_value()
    {
        var empty = Position(pnlFraction: -0.05m) with { MarketValue = 0m };
        Assert.False(ExitManager.Evaluate(empty, Exits, Windows, Session, MidSession).ShouldExit);
    }

    [Fact]
    public void Still_flattens_when_the_broker_omits_the_quantity()
    {
        // Presence in the positions list with a market value is what proves
        // there is something to close. Requiring qty would mean a missing
        // field silently exempts a position from the flatten.
        var noQty = Position(pnlFraction: 0m) with { Quantity = null };
        var decision = ExitManager.Evaluate(noQty, Exits, Windows, Session, Session.CloseUtc.AddMinutes(-10));
        Assert.True(decision.ShouldExit);
        Assert.Equal(ExitReason.SessionClose, decision.Reason);
    }

    private static DateTimeOffset MidSession => Session.OpenUtc.AddHours(2);

    private static PositionSnapshot Position(decimal pnlFraction, DateTimeOffset? openedAt = null) =>
        new("AAPL", 0.05m, 10m, 200m, 200m * (1m + pnlFraction), 10m * pnlFraction, pnlFraction, openedAt);
}

/// <summary>
/// A refused quote is refused for a reason, and the reason is what tells an
/// operator whether the market was wide or the data feed was thin.
/// </summary>
public sealed class MarketDataValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 15, 0, 0, TimeSpan.Zero);

    private static ClaudeTradingAgent.MarketData.QuoteSnapshot Quote(
        decimal bid, decimal ask, int ageSeconds = 0) =>
        new("AAPL", bid, ask, Now.AddSeconds(-ageSeconds));

    [Fact]
    public void Accepts_a_tight_fresh_quote()
    {
        var ex = Record.Exception(() => ClaudeTradingAgent.MarketData.MarketDataValidator
            .ValidateQuote(Quote(200.00m, 200.02m), Now, TimeSpan.FromSeconds(15), 25m));
        Assert.Null(ex);
    }

    [Fact]
    public void Labels_a_wide_spread_as_such()
    {
        // 200.00 / 201.00 is ~50bps against a 25bps policy — the shape of an
        // IEX-only quote on a symbol that trades thinly there.
        var ex = Assert.Throws<ClaudeTradingAgent.MarketData.MarketDataException>(() =>
            ClaudeTradingAgent.MarketData.MarketDataValidator
                .ValidateQuote(Quote(200.00m, 201.00m), Now, TimeSpan.FromSeconds(15), 25m));
        Assert.Equal(ClaudeTradingAgent.MarketData.MarketDataRejection.WideSpread, ex.Reason);
    }

    [Fact]
    public void Labels_a_stale_quote_separately_from_a_wide_one()
    {
        // These need different responses: one is a data plan, the other is a
        // connection. Counting them together hides both.
        var ex = Assert.Throws<ClaudeTradingAgent.MarketData.MarketDataException>(() =>
            ClaudeTradingAgent.MarketData.MarketDataValidator
                .ValidateQuote(Quote(200.00m, 200.02m, ageSeconds: 60), Now, TimeSpan.FromSeconds(15), 25m));
        Assert.Equal(ClaudeTradingAgent.MarketData.MarketDataRejection.Stale, ex.Reason);
    }

    [Fact]
    public void Still_fails_closed_for_callers_catching_the_base_type()
    {
        // Existing handlers catch InvalidOperationException. The typed reason
        // must not change what they catch.
        Assert.Throws<ClaudeTradingAgent.MarketData.MarketDataException>(() =>
            ClaudeTradingAgent.MarketData.MarketDataValidator
                .ValidateQuote(Quote(0m, 200.02m), Now, TimeSpan.FromSeconds(15), 25m));

        var caught = Record.Exception(() =>
        {
            try
            {
                ClaudeTradingAgent.MarketData.MarketDataValidator
                    .ValidateQuote(Quote(0m, 200.02m), Now, TimeSpan.FromSeconds(15), 25m);
            }
            catch (InvalidOperationException) { /* the fail-closed path still sees it */ }
        });
        Assert.Null(caught);
    }
}
