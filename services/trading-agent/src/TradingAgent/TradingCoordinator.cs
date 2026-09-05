using ClaudeTradingAgent.Execution;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;

namespace ClaudeTradingAgent.TradingAgent;

public sealed record TradingRunResult(
    string Status,
    RiskDecision Decision,
    BrokerOrderResult? BrokerOrder = null)
{
    public string Code => Decision.Code;
    public string Message => Decision.Reason;
    public bool Submitted => Status == "SUBMITTED";
}

/// <summary>
/// The single path from a proposal to the broker. Both entries and exits go
/// through the risk engine here; the intent only decides which broker call
/// carries out an order the engine has already approved.
/// </summary>
public sealed class TradingCoordinator(RiskEngine riskEngine, IOrderExecutor executor)
{
    public async Task<TradingRunResult> ProcessAsync(
        StrategySignal proposal,
        AccountRiskState accountState,
        RiskPolicy policy,
        IReadOnlySet<string> allowlist,
        DateTimeOffset now,
        OrderIntent intent = OrderIntent.Entry,
        CancellationToken cancellationToken = default)
    {
        var decision = riskEngine.Evaluate(proposal, accountState, policy, allowlist, now, intent);
        if (!decision.Approved || decision.Order is null)
            return new TradingRunResult("REJECTED", decision);

        var brokerOrder = intent == OrderIntent.Exit
            ? await executor.LiquidatePositionAsync(decision.Order, cancellationToken)
            : await executor.SubmitApprovedOrderAsync(decision.Order, cancellationToken);

        return new TradingRunResult("SUBMITTED", decision, brokerOrder);
    }
}
