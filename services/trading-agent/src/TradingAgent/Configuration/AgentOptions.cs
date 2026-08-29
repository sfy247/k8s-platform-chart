namespace ClaudeTradingAgent.TradingAgent.Configuration;

/// <summary>
/// Runtime settings, read from the environment once at startup.
///
/// Credentials come from a Kubernetes Secret; everything that governs
/// BEHAVIOUR — the mode, the kill switch, the broker endpoint — comes from
/// configuration that is reviewable in a pull request. That split is
/// deliberate: flipping this system to live trading must be a code change
/// someone reads, never an invisible edit to a Secret.
/// </summary>
public sealed record AgentOptions
{
    public const string SectionName = "Agent";

    public string TradingMode { get; init; } = "PAPER";
    public bool TradingEnabled { get; init; }
    public string AlpacaTradingBaseUrl { get; init; } = "https://paper-api.alpaca.markets";
    public string AlpacaDataBaseUrl { get; init; } = "https://data.alpaca.markets";
    public string AlpacaApiKeyId { get; init; } = string.Empty;
    public string AlpacaApiSecretKey { get; init; } = string.Empty;
    public string TradingConfigPath { get; init; } = "config/trading.json";
    public string SymbolConfigPath { get; init; } = "config/symbols.json";
    public int EvaluationIntervalSeconds { get; init; } = 60;
    public int BrokerTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Postgres connection string for the decision audit. Supplied by the
    /// trading-db Secret as ConnectionStrings__Default. Empty means the
    /// agent runs without durable history.
    /// </summary>
    public string DatabaseConnectionString { get; init; } = string.Empty;

    public bool HasDatabase => !string.IsNullOrWhiteSpace(DatabaseConnectionString);

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(AlpacaApiKeyId) && !string.IsNullOrWhiteSpace(AlpacaApiSecretKey);

    /// <summary>Fail fast on anything that would make the agent unsafe or useless.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!string.Equals(TradingMode, "PAPER", StringComparison.OrdinalIgnoreCase))
            errors.Add($"TRADING_MODE must be PAPER; got '{TradingMode}'. Live trading requires a deliberate code change.");

        if (EvaluationIntervalSeconds < 5)
            errors.Add("EVALUATION_INTERVAL_SECONDS must be at least 5.");

        if (BrokerTimeoutSeconds is < 1 or > 60)
            errors.Add("BROKER_TIMEOUT_SECONDS must be between 1 and 60.");

        // Validated here as well as in Execution.PaperEndpointGuard, so a bad
        // endpoint is refused at startup rather than at the first order.
        if (!Uri.TryCreate(AlpacaTradingBaseUrl, UriKind.Absolute, out var trading)
            || trading.Scheme != Uri.UriSchemeHttps
            || !trading.Host.Equals("paper-api.alpaca.markets", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"ALPACA_TRADING_BASE_URL must be https://paper-api.alpaca.markets; got '{AlpacaTradingBaseUrl}'.");
        }

        if (TradingEnabled && !HasCredentials)
            errors.Add("TRADING_ENABLED is true but Alpaca credentials are missing.");

        return errors;
    }
}
