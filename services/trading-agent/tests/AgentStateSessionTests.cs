using ClaudeTradingAgent.TradingAgent.Observability;
using Xunit;

namespace ClaudeTradingAgent.Tests;

/// <summary>State that must outlive one cycle: the daily lockout, unclear orders, audited outcomes.</summary>
public sealed class AgentStateSessionTests
{
    private static readonly DateOnly Monday = new(2026, 9, 14);
    private static readonly DateOnly Tuesday = new(2026, 9, 15);

    [Fact]
    public void The_daily_lockout_latches_once_and_holds_for_the_session()
    {
        var state = new AgentState();
        Assert.False(state.IsLockedOut(Monday));
        Assert.True(state.LatchDailyLockout(Monday));    // first trip is reported
        Assert.False(state.LatchDailyLockout(Monday));   // not reported again
        Assert.True(state.IsLockedOut(Monday));
    }

    [Fact]
    public void A_new_session_clears_the_lockout()
    {
        var state = new AgentState();
        state.LatchDailyLockout(Monday);
        Assert.False(state.IsLockedOut(Tuesday));
    }

    [Fact]
    public void Uncertain_orders_are_tracked_until_resolved()
    {
        var state = new AgentState();
        state.MarkUncertain("cta-1", "AAPL");
        Assert.Single(state.UncertainOrders());
        state.ResolveUncertain("cta-1");
        Assert.Empty(state.UncertainOrders());
    }

    [Fact]
    public void An_outcome_counts_as_audited_only_once_marked()
    {
        var state = new AgentState();
        Assert.False(state.IsOutcomeAudited(Monday, "brk-1", "filled"));
        state.MarkOutcomeAudited(Monday, "brk-1", "filled");
        Assert.True(state.IsOutcomeAudited(Monday, "brk-1", "filled"));
        // A later status for the same order is a new outcome.
        Assert.False(state.IsOutcomeAudited(Monday, "brk-1", "canceled"));
    }
}
