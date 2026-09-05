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
    /// Which Alpaca market data feed to quote from.
    ///
    /// This is an account-plan property, like the credentials and the
    /// endpoints, which is why it lives in the environment rather than in
    /// trading.json — buying a data subscription should not require
    /// rebuilding the image.
    ///
    /// "iex" is the free feed and covers roughly 2-3% of US equity volume.
    /// When IEX has no size at the inside, an IEX-only quote reads far wider
    /// than the real market, and this agent correctly refuses to trade on it.
    /// Measured over one session on AAPL/MSFT/GOOGL/AMZN/NVDA, that discarded
    /// 20% of all evaluations — 49% on MSFT and 0% on AAPL, a spread that
    /// tracks IEX liquidity rather than anything about the market.
    ///
    /// "sip" is the consolidated tape across every venue and requires a paid
    /// Alpaca subscription. Switch to it before drawing conclusions about
    /// whether a strategy works.
    /// </summary>
    public string AlpacaDataFeed { get; init; } = DefaultFeed;

    public const string DefaultFeed = "iex";

    /// <summary>
    /// Only the two feeds that make sense for intraday trading. Alpaca also
    /// offers delayed feeds; they are excluded deliberately, because a
    /// 15-minute-old quote is not a quote a day-trading agent should ever be
    /// pointed at by accident.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedFeeds = ["iex", "sip"];

    /// <summary>The feed in the form the Alpaca API expects.</summary>
    public string NormalisedDataFeed => AlpacaDataFeed.Trim().ToLowerInvariant();

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

        if (!SupportedFeeds.Contains(NormalisedDataFeed))
        {
            errors.Add(
                $"ALPACA_DATA_FEED must be one of {string.Join(", ", SupportedFeeds)}; got '{AlpacaDataFeed}'. "
                + "Delayed feeds are excluded deliberately: this agent trades intraday.");
        }

        if (TradingEnabled && !HasCredentials)
            errors.Add("TRADING_ENABLED is true but Alpaca credentials are missing.");

        return errors;
    }
}
