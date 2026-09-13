namespace ClaudeTradingAgent.Persistence;

public interface IDecisionStore
{
    Task InitialiseAsync(CancellationToken cancellationToken = default);
    Task RecordAsync(DecisionRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges what the broker finally did with an order into its decision row.
    /// Returns the number of rows updated; zero means the order is not one
    /// this agent recorded.
    /// </summary>
    Task<int> RecordOrderOutcomeAsync(OrderOutcome outcome, CancellationToken cancellationToken = default);
}

/// <summary>Final broker state of an order: fill, cancellation, rejection or expiry.</summary>
public sealed record OrderOutcome(
    string BrokerOrderId,
    string Status,
    decimal? FilledQuantity,
    decimal? FilledAveragePrice,
    DateTimeOffset? FilledAtUtc,
    DateTimeOffset? ClosedAtUtc);

/// <summary>
/// Used when no connection string is configured. The agent still runs and
/// still logs; it simply keeps no durable history.
///
/// This is a deliberate choice for a lab: a missing database should not stop
/// the agent evaluating. If trading is ever enabled, that trade-off inverts —
/// see the comment in PostgresDecisionStore.
/// </summary>
public sealed class NullDecisionStore : IDecisionStore
{
    public Task InitialiseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RecordAsync(DecisionRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<int> RecordOrderOutcomeAsync(OrderOutcome outcome, CancellationToken cancellationToken = default) => Task.FromResult(0);
}
