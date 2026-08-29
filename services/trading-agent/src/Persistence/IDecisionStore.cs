namespace ClaudeTradingAgent.Persistence;

public interface IDecisionStore
{
    Task InitialiseAsync(CancellationToken cancellationToken = default);
    Task RecordAsync(DecisionRecord record, CancellationToken cancellationToken = default);
}

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
}
