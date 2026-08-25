using ClaudeTradingAgent.Execution;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.TradingAgent;

public sealed record TradingRunResult(string Status, string Code, string Message, BrokerOrderResult? BrokerOrder = null);

public sealed class TradingCoordinator(RiskEngine riskEngine, IOrderExecutor executor)
{
    public async Task<TradingRunResult> ProcessAsync(
        StrategySignal proposal,
        AccountRiskState accountState,
        RiskPolicy policy,
        IReadOnlySet<string> allowlist,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var decision = riskEngine.Evaluate(proposal, accountState, policy, allowlist, now);
        if (!decision.Approved || decision.Order is null)
            return new TradingRunResult("REJECTED", decision.Code, decision.Reason);

        var brokerOrder = await executor.SubmitApprovedOrderAsync(decision.Order, cancellationToken);
        return new TradingRunResult("SUBMITTED", "BROKER_ACCEPTED", "Approved paper order submitted.", brokerOrder);
    }
}
