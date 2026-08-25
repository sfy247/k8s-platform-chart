namespace ClaudeTradingAgent.TradingAgent.Observability;

/// <summary>
/// What readiness reports on.
///
/// The agent is ready once a full evaluation cycle has completed. Before
/// that it may be unable to reach the broker, and a pod that cannot see the
/// market should not be reporting itself fit.
/// </summary>
public sealed class AgentState
{
    private readonly object _gate = new();   // System.Threading.Lock is .NET 9
    private DateTimeOffset? _lastSuccess;
    private string _lastOutcome = "no cycle has run yet";
    private string? _lastError;
    private int _consecutiveFailures;

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
            // Truncated: an error message is diagnostic context, not a payload,
            // and broker errors can be long.
            _lastError = error.Length > 300 ? error[..300] : error;
            _consecutiveFailures++;
        }
    }
}
