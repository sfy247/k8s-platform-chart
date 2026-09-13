using ClaudeTradingAgent.RiskManagement;

namespace ClaudeTradingAgent.Execution;

public interface IOrderExecutor
{
    Task<BrokerOrderResult> SubmitApprovedOrderAsync(ApprovedOrder order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the entire position in a symbol at market. The broker closes the
    /// quantity it actually holds, so no fractional remainder survives the day.
    /// </summary>
    Task<BrokerOrderResult> LiquidatePositionAsync(ApprovedOrder order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks an order up by its client_order_id. Null means the broker
    /// confirms no such order exists; a throw means the answer is unknown.
    /// </summary>
    Task<BrokerOrderResult?> GetOrderByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken = default);
}
