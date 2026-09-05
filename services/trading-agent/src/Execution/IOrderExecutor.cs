using ClaudeTradingAgent.RiskManagement;

namespace ClaudeTradingAgent.Execution;

public interface IOrderExecutor
{
    Task<BrokerOrderResult> SubmitApprovedOrderAsync(ApprovedOrder order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the entire position in a symbol at market.
    ///
    /// Separate from <see cref="SubmitApprovedOrderAsync"/> because a
    /// notional sell leaves fractional dust behind when the position has
    /// drifted, and "almost flat" is not flat. The broker closes the whole
    /// quantity it actually holds, which is the only number that matters at
    /// the end of a day-trading session.
    /// </summary>
    Task<BrokerOrderResult> LiquidatePositionAsync(ApprovedOrder order, CancellationToken cancellationToken = default);
}
