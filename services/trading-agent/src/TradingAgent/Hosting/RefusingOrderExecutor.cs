using ClaudeTradingAgent.Execution;
using ClaudeTradingAgent.RiskManagement;

namespace ClaudeTradingAgent.TradingAgent.Hosting;

/// <summary>
/// The executor installed whenever trading is disabled.
///
/// The risk engine already rejects every proposal with KILL_SWITCH before
/// execution is reached, so this should be unreachable. It exists because
/// "should be unreachable" is not a control: if a future change ever routes
/// around the risk engine, this throws instead of quietly placing an order.
/// </summary>
public sealed class RefusingOrderExecutor(ILogger<RefusingOrderExecutor> logger) : IOrderExecutor
{
    public Task<BrokerOrderResult> SubmitApprovedOrderAsync(ApprovedOrder order, CancellationToken cancellationToken = default) =>
        Refuse(order, "Order submission");

    public Task<BrokerOrderResult> LiquidatePositionAsync(ApprovedOrder order, CancellationToken cancellationToken = default) =>
        Refuse(order, "Position liquidation");

    private Task<BrokerOrderResult> Refuse(ApprovedOrder order, string what)
    {
        logger.LogCritical(
            "{What} attempted while trading is disabled: {ClientOrderId} {Symbol}. This indicates the risk engine was bypassed.",
            what, order.ClientOrderId, order.Symbol);

        throw new InvalidOperationException(
            "Trading is disabled. Enable it deliberately in configuration; the executor refuses to reach the broker.");
    }
}
