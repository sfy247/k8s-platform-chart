namespace ClaudeTradingAgent.TradingAgent.Observability;

/// <summary>
/// What readiness reports on, plus the few facts that must outlive a single
/// evaluation cycle: the daily-loss lockout, orders whose broker status is
/// unconfirmed, and which order outcomes have already been audited.
///
/// In memory by design. A restarted pod re-derives the lockout from broker
/// P&L on its first cycle, and re-reads today's orders from the broker.
/// </summary>
public sealed class AgentState
{
    private readonly object _gate = new();   // System.Threading.Lock is .NET 9
    private DateTimeOffset? _lastSuccess;
    private string _lastOutcome = "no cycle has run yet";
    private string? _lastError;
    private int _consecutiveFailures;
    private string _sessionState = "UNKNOWN";
    private DateOnly? _lockoutDate;
    private readonly Dictionary<string, string> _uncertainOrders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _auditedOutcomes = new(StringComparer.Ordinal);
    private DateOnly? _auditedDate;

    public bool IsReady
    {
        get { lock (_gate) { return _lastSuccess is not null && _consecutiveFailures < 3; } }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                ready = _lastSuccess is not null && _consecutiveFailures < 3,
                lastSuccessfulCycleUtc = _lastSuccess,
                lastOutcome = _lastOutcome,
                consecutiveFailures = _consecutiveFailures,
                lastError = _lastError,
                sessionState = _sessionState,
                dailyLossLockout = _lockoutDate is not null,
                uncertainOrders = _uncertainOrders.Count,
            };
        }
    }

    public void RecordCycleSuccess(int symbolsEvaluated, string outcome)
    {
        lock (_gate)
        {
            _lastSuccess = DateTimeOffset.UtcNow;
            _lastOutcome = $"{outcome} ({symbolsEvaluated} symbol(s))";
            _lastError = null;
            _consecutiveFailures = 0;
        }
    }

    public void RecordCycleFailure(string error)
    {
        lock (_gate)
        {
            _lastError = error.Length > 300 ? error[..300] : error;
            _consecutiveFailures++;
        }
    }

    public void RecordSessionState(string label)
    {
        lock (_gate) { _sessionState = label; }
    }

    // ── Daily loss lockout ────────────────────────────────────────────────
    // v3: once the daily loss limit is reached, new entries stay blocked for
    // the rest of the session even if P&L recovers.

    /// <summary>Latches the lockout for a session. True only the first time.</summary>
    public bool LatchDailyLockout(DateOnly sessionDate)
    {
        lock (_gate)
        {
            if (_lockoutDate == sessionDate) return false;
            _lockoutDate = sessionDate;
            return true;
        }
    }

    public bool IsLockedOut(DateOnly sessionDate)
    {
        lock (_gate)
        {
            if (_lockoutDate is { } d && d != sessionDate) _lockoutDate = null;   // a new session clears it
            return _lockoutDate == sessionDate;
        }
    }

    // ── Orders with unconfirmed broker status ─────────────────────────────

    public void MarkUncertain(string clientOrderId, string symbol)
    {
        lock (_gate) { _uncertainOrders[clientOrderId] = symbol; }
    }

    public void ResolveUncertain(string clientOrderId)
    {
        lock (_gate) { _uncertainOrders.Remove(clientOrderId); }
    }

    public IReadOnlyList<(string ClientOrderId, string Symbol)> UncertainOrders()
    {
        lock (_gate) { return _uncertainOrders.Select(kv => (kv.Key, kv.Value)).ToList(); }
    }

    // ── Order outcomes already written to the audit ───────────────────────

    /// <summary>Whether this order outcome has already been written for the session.</summary>
    public bool IsOutcomeAudited(DateOnly sessionDate, string brokerOrderId, string status)
    {
        lock (_gate)
        {
            if (_auditedDate != sessionDate) { _auditedOutcomes.Clear(); _auditedDate = sessionDate; }
            return _auditedOutcomes.Contains($"{brokerOrderId}:{status}");
        }
    }

    /// <summary>Call only after the write succeeds, so a failed write is retried next cycle.</summary>
    public void MarkOutcomeAudited(DateOnly sessionDate, string brokerOrderId, string status)
    {
        lock (_gate)
        {
            if (_auditedDate != sessionDate) { _auditedOutcomes.Clear(); _auditedDate = sessionDate; }
            _auditedOutcomes.Add($"{brokerOrderId}:{status}");
        }
    }
}
