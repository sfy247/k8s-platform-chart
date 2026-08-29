using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ClaudeTradingAgent.Persistence;

/// <summary>
/// Audit of every evaluation: one row per decision.
///
/// Rows without an order id are written once and never touched again. Rows
/// WITH one are written twice — an intent record before the order is sent to
/// the broker, then the outcome merged in when the broker answers. That
/// ordering is the point: if the process dies mid-submission there is still
/// a record that an order was attempted, which is the case an audit exists
/// for. COALESCE means the outcome never erases what the intent recorded.
///
/// Deliberately not an ORM: this is two statements against one table.
/// Change tracking and entity graphs would be cost without benefit.
/// </summary>
public sealed class PostgresDecisionStore(
    NpgsqlDataSource dataSource,
    ILogger<PostgresDecisionStore> logger) : IDecisionStore
{
    // A lock id unique to this migration. Two replicas starting together
    // would otherwise race to create the same table.
    private const long MigrationLockId = 8_531_240_119;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS trading_decision (
            id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            decided_at          timestamptz NOT NULL,
            symbol              text        NOT NULL,

            strategy_name       text,
            action              text,
            proposed_notional   numeric(18,4),
            confidence          numeric(6,4),
            reasoning_summary   text,
            data_timestamp      timestamptz,

            approved            boolean     NOT NULL,
            decision_code       text        NOT NULL,
            decision_reason     text        NOT NULL,
            client_order_id     text,

            broker_order_id     text,
            broker_status       text,
            filled_quantity     numeric(18,6),
            filled_avg_price    numeric(18,6),

            trading_enabled     boolean     NOT NULL,
            market_open         boolean     NOT NULL,
            pod                 text        NOT NULL,

            -- Idempotency: the broker rejects duplicate client_order_id, and
            -- so does the audit trail. A retried submission cannot appear as
            -- two orders here.
            CONSTRAINT trading_decision_client_order_id_key UNIQUE (client_order_id)
        );

        CREATE INDEX IF NOT EXISTS trading_decision_decided_at_idx
            ON trading_decision (decided_at DESC);

        CREATE INDEX IF NOT EXISTS trading_decision_symbol_decided_at_idx
            ON trading_decision (symbol, decided_at DESC);

        -- Partial: approvals are rare and are what an audit actually looks
        -- for, so indexing only those keeps it small.
        CREATE INDEX IF NOT EXISTS trading_decision_approved_idx
            ON trading_decision (decided_at DESC) WHERE approved;
        """;

    private const string Insert = """
        INSERT INTO trading_decision (
            decided_at, symbol,
            strategy_name, action, proposed_notional, confidence, reasoning_summary, data_timestamp,
            approved, decision_code, decision_reason, client_order_id,
            broker_order_id, broker_status, filled_quantity, filled_avg_price,
            trading_enabled, market_open, pod)
        VALUES (
            @DecidedAtUtc, @Symbol,
            @StrategyName, @Action, @ProposedNotional, @Confidence, @ReasoningSummary, @DataTimestampUtc,
            @Approved, @DecisionCode, @DecisionReason, @ClientOrderId,
            @BrokerOrderId, @BrokerStatus, @FilledQuantity, @FilledAveragePrice,
            @TradingEnabled, @MarketOpen, @Pod)
        ON CONFLICT (client_order_id) DO UPDATE SET
            decision_code    = EXCLUDED.decision_code,
            decision_reason  = EXCLUDED.decision_reason,
            broker_order_id  = COALESCE(EXCLUDED.broker_order_id,  trading_decision.broker_order_id),
            broker_status    = COALESCE(EXCLUDED.broker_status,    trading_decision.broker_status),
            filled_quantity  = COALESCE(EXCLUDED.filled_quantity,  trading_decision.filled_quantity),
            filled_avg_price = COALESCE(EXCLUDED.filled_avg_price, trading_decision.filled_avg_price);
        """;

    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        // Applied at startup rather than by a separate job. That is a
        // deliberate trade-off for an append-only audit table whose migration
        // is a CREATE TABLE IF NOT EXISTS: it cannot lock a busy table or
        // rewrite data. A schema change that alters existing rows should NOT
        // be added here — it belongs in a migration job run before rollout.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Session-scoped advisory lock: released when the connection closes,
        // so a crashed pod cannot leave the lock held.
        await connection.ExecuteAsync(
            new CommandDefinition("SELECT pg_advisory_lock(@id)",
                new { id = MigrationLockId }, cancellationToken: cancellationToken));
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(Schema, cancellationToken: cancellationToken));
            logger.LogInformation("Decision audit schema is present.");
        }
        finally
        {
            await connection.ExecuteAsync(
                new CommandDefinition("SELECT pg_advisory_unlock(@id)",
                    new { id = MigrationLockId }, cancellationToken: cancellationToken));
        }
    }

    public async Task RecordAsync(DecisionRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(Insert, new
        {
            record.DecidedAtUtc,
            record.Symbol,
            record.StrategyName,
            Action = record.Action?.ToString(),
            record.ProposedNotional,
            record.Confidence,
            record.ReasoningSummary,
            record.DataTimestampUtc,
            record.Approved,
            record.DecisionCode,
            record.DecisionReason,
            record.ClientOrderId,
            record.BrokerOrderId,
            record.BrokerStatus,
            record.FilledQuantity,
            record.FilledAveragePrice,
            record.TradingEnabled,
            record.MarketOpen,
            record.Pod,
        }, cancellationToken: cancellationToken));
    }
}
