using ClaudeTradingAgent.RiskManagement;

namespace ClaudeTradingAgent.Execution;

public interface IOrderExecutor
{
    Task<BrokerOrderResult> SubmitApprovedOrderAsync(ApprovedOrder order, CancellationToken cancellationToken = default);
}
