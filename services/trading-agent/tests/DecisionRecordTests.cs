using ClaudeTradingAgent.Execution;
using ClaudeTradingAgent.Persistence;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;
using Xunit;

namespace TradingAgent.Tests;

/// <summary>
/// The audit row is the durable record of what the agent decided, so what it
/// captures — and what it captures when things went wrong — is worth pinning.
/// </summary>
public sealed class DecisionRecordTests
{
    private static StrategySignal Proposal(TradeAction action = TradeAction.Buy) =>
        new("AAPL", action, 10m, 0.82m, "momentum-v1", "Momentum criteria confirmed.", DateTimeOffset.UtcNow);

    [Fact]
    public void NoDataRecordsTheRejectionWithoutAProposal()
    {
        var record = DecisionRecord.NoData("MSFT", "Quote spread exceeds policy.", false, true, "pod-1");

        Assert.Equal("NO_DATA", record.DecisionCode);
        Assert.False(record.Approved);
        Assert.Equal("MSFT", record.Symbol);
        // There was no proposal, so these must stay empty rather than
        // recording a zero that reads like a real decision.
        Assert.Null(record.Action);
        Assert.Null(record.ProposedNotional);
        Assert.Null(record.StrategyName);
    }

    [Fact]
    public void RejectedDecisionIsRecordedWithItsCode()
    {
        var decision = new RiskDecision(false, "KILL_SWITCH", "Trading is disabled.");
        var record = DecisionRecord.From(Proposal(), decision, null, false, true, "pod-1");

        Assert.False(record.Approved);
        Assert.Equal("KILL_SWITCH", record.DecisionCode);
        Assert.Equal(TradeAction.Buy, record.Action);
        Assert.Equal(0.82m, record.Confidence);
        // Nothing reached the broker, so there is nothing to record about one.
        Assert.Null(record.BrokerOrderId);
        Assert.Null(record.ClientOrderId);
    }

    [Fact]
    public void ApprovedDecisionCapturesTheOrderAndTheFill()
    {
        var order = new ApprovedOrder("cta-123", "AAPL", TradeAction.Buy, 10m, DateTimeOffset.UtcNow);
        var decision = new RiskDecision(true, "APPROVED", "All deterministic risk checks passed.", order);
        var broker = new BrokerOrderResult("brk-9", "cta-123", "AAPL", "filled", 0.02m, 500m, DateTimeOffset.UtcNow);

        var record = DecisionRecord.From(Proposal(), decision, broker, true, true, "pod-1");

        Assert.True(record.Approved);
        Assert.Equal("cta-123", record.ClientOrderId);
        Assert.Equal("brk-9", record.BrokerOrderId);
        Assert.Equal("filled", record.BrokerStatus);
        Assert.Equal(0.02m, record.FilledQuantity);
        Assert.Equal(500m, record.FilledAveragePrice);
    }

    [Fact]
    public void ContextIsCapturedSoARowCanBeReadYearsLater()
    {
        var decision = new RiskDecision(false, "MARKET_CLOSED", "Market is closed.");
        var record = DecisionRecord.From(Proposal(), decision, null, tradingEnabled: false, marketOpen: false, "pod-7");

        // Without these, a historic row cannot be distinguished from one
        // taken under different configuration.
        Assert.False(record.TradingEnabled);
        Assert.False(record.MarketOpen);
        Assert.Equal("pod-7", record.Pod);
    }

    [Fact]
    public async Task TheNullStoreIsSafeToUseWhenNoDatabaseIsConfigured()
    {
        var store = new NullDecisionStore();
        await store.InitialiseAsync();
        await store.RecordAsync(DecisionRecord.NoData("AAPL", "x", false, true, "pod-1"));
    }
}
