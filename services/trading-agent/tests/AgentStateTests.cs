using ClaudeTradingAgent.TradingAgent.Observability;
using Xunit;

namespace TradingAgent.Tests;

/// <summary>
/// Readiness decides whether Kubernetes sends this pod traffic and whether
/// an operator believes it is working, so its transitions are worth pinning.
/// </summary>
public sealed class AgentStateTests
{
    [Fact]
    public void IsNotReadyBeforeAnyCycleHasRun()
        => Assert.False(new AgentState().IsReady);

    [Fact]
    public void BecomesReadyAfterASuccessfulCycle()
    {
        var state = new AgentState();
        state.RecordCycleSuccess(5, "evaluated");
        Assert.True(state.IsReady);
    }

    [Fact]
    public void SurvivesOccasionalFailures()
    {
        var state = new AgentState();
        state.RecordCycleSuccess(5, "evaluated");
        state.RecordCycleFailure("broker timeout");
        state.RecordCycleFailure("broker timeout");
        // Two failures is a blip, not an outage — the broker is allowed a
        // bad minute without the pod being taken out of service.
        Assert.True(state.IsReady);
    }

    [Fact]
    public void GoesUnreadyAfterSustainedFailure()
    {
        var state = new AgentState();
        state.RecordCycleSuccess(5, "evaluated");
        state.RecordCycleFailure("broker timeout");
        state.RecordCycleFailure("broker timeout");
        state.RecordCycleFailure("broker timeout");
        Assert.False(state.IsReady);
    }

    [Fact]
    public void RecoversAfterASuccessfulCycle()
    {
        var state = new AgentState();
        for (var i = 0; i < 5; i++) state.RecordCycleFailure("broker timeout");
        state.RecordCycleSuccess(5, "evaluated");
        Assert.True(state.IsReady);
    }
}
