using ClaudeTradingAgent.Execution;
using ClaudeTradingAgent.Persistence;
using ClaudeTradingAgent.RiskManagement;

namespace ClaudeTradingAgent.TradingAgent.Hosting;

/// <summary>
/// Records that an order is about to be sent, before sending it.
///
/// Writing the audit only after the broker replies leaves a window where an
/// order exists at the broker and nothing locally knows about it — a crash,
/// a timeout or a pod eviction in that window loses the record of a real
/// order. Writing intent first inverts the failure: the worst case becomes
/// an intent row with no outcome, which is a reconciliation task rather than
/// an invisible order.
///
/// If the intent cannot be written, the order is NOT sent. That is the
/// fail-closed choice: an unauditable order is worse than a missed trade.
/// </summary>
public sealed class AuditingOrderExecutor(
    IOrderExecutor inner,
    IDecisionStore store,
    ILogger<AuditingOrderExecutor> logger) : IOrderExecutor
{
    public async Task<BrokerOrderResult> SubmitApprovedOrderAsync(
        ApprovedOrder order, CancellationToken cancellationToken = default)
    {
        var intent = new DecisionRecord
        {
            DecidedAtUtc = order.ApprovedAtUtc,
            Symbol = order.Symbol,
            Action = order.Action,
            ProposedNotional = order.Notional,
            Approved = true,
            DecisionCode = "SUBMITTING",
            DecisionReason = "Approved by the risk engine; sending to the broker.",
            ClientOrderId = order.ClientOrderId,
            TradingEnabled = true,
            MarketOpen = true,
            Pod = Environment.MachineName,
        };

        try
        {
            await store.RecordAsync(intent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Refusing to submit {ClientOrderId}: the intent could not be recorded.",
                order.ClientOrderId);
            throw new InvalidOperationException(
                "Order not submitted because it could not be recorded in the audit trail.", ex);
        }

        return await inner.SubmitApprovedOrderAsync(order, cancellationToken);
    }
}
