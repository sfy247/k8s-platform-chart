using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;
using ClaudeTradingAgent.TradingAgent.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradingAgent.Tests;

public sealed class RefusingOrderExecutorTests
{
    [Fact]
    public async Task RefusesToSubmitEvenAFullyFormedOrder()
    {
        var executor = new RefusingOrderExecutor(NullLogger<RefusingOrderExecutor>.Instance);
        var order = new ApprovedOrder("cta-test", "AAPL", TradeAction.Buy, 10m, DateTimeOffset.UtcNow);

        // The risk engine rejects with KILL_SWITCH long before this, so
        // reaching here means something bypassed it. It must throw, not trade.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.SubmitApprovedOrderAsync(order));
    }
}
