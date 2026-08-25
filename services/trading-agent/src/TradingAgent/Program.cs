using ClaudeTradingAgent.Execution;
using ClaudeTradingAgent.MarketData;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;
using ClaudeTradingAgent.TradingAgent;
using ClaudeTradingAgent.TradingAgent.Configuration;
using ClaudeTradingAgent.TradingAgent.Hosting;
using ClaudeTradingAgent.TradingAgent.Observability;
using Microsoft.Extensions.Logging.Console;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// ── Logging ──────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.FormatterName = JsonLogFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<JsonLogFormatter, ConsoleFormatterOptions>();

// Kubernetes probes every few seconds, and ASP.NET logs four lines per
// request at Information. That is thousands of lines a day saying nothing,
// which costs money in the log store and buries the decisions that matter.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

// ── Configuration ────────────────────────────────────────────────────────
// Environment variables only: the deployment supplies behaviour through
// values.yaml and credentials through a Secret, and neither is baked in.
var options = new AgentOptions
{
    TradingMode = builder.Configuration["TRADING_MODE"] ?? "PAPER",
    TradingEnabled = builder.Configuration.GetValue("TRADING_ENABLED", false),
    AlpacaTradingBaseUrl = builder.Configuration["ALPACA_TRADING_BASE_URL"] ?? "https://paper-api.alpaca.markets",
    AlpacaDataBaseUrl = builder.Configuration["ALPACA_DATA_BASE_URL"] ?? "https://data.alpaca.markets",
    AlpacaApiKeyId = builder.Configuration["ALPACA_API_KEY_ID"] ?? string.Empty,
    AlpacaApiSecretKey = builder.Configuration["ALPACA_API_SECRET_KEY"] ?? string.Empty,
    TradingConfigPath = builder.Configuration["TRADING_CONFIG_PATH"] ?? "config/trading.json",
    SymbolConfigPath = builder.Configuration["SYMBOL_CONFIG_PATH"] ?? "config/symbols.json",
    EvaluationIntervalSeconds = builder.Configuration.GetValue("EVALUATION_INTERVAL_SECONDS", 60),
    BrokerTimeoutSeconds = builder.Configuration.GetValue("BROKER_TIMEOUT_SECONDS", 10),
};

var configErrors = options.Validate();
if (configErrors.Count > 0)
{
    // Fail fast and loudly. A trading agent running on configuration it does
    // not understand is worse than one that refuses to start.
    foreach (var error in configErrors) Console.Error.WriteLine($"CONFIG ERROR: {error}");
    return 1;
}

var policies = TradingPolicySet.Load(options);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(policies);
builder.Services.AddSingleton<AgentState>();
builder.Services.AddSingleton<MomentumStrategy>();
builder.Services.AddSingleton<RiskEngine>();

// ── Broker clients ───────────────────────────────────────────────────────
var timeout = TimeSpan.FromSeconds(options.BrokerTimeoutSeconds);

builder.Services.AddHttpClient<IMarketDataProvider, AlpacaMarketDataProvider>(c => c.Timeout = timeout)
    .AddTypedClient((http, _) => (IMarketDataProvider)new AlpacaMarketDataProvider(
        http, options.AlpacaApiKeyId, options.AlpacaApiSecretKey,
        options.AlpacaDataBaseUrl, options.AlpacaTradingBaseUrl));

builder.Services.AddHttpClient<AccountSnapshotProvider>(c => c.Timeout = timeout)
    .AddTypedClient((http, _) => new AccountSnapshotProvider(
        http, options.AlpacaApiKeyId, options.AlpacaApiSecretKey, options.AlpacaTradingBaseUrl));

// The executor is chosen by configuration, not by a runtime branch inside
// the trading path: when trading is disabled there is no code path that can
// reach a broker at all.
if (policies.Risk.TradingEnabled)
{
    builder.Services.AddHttpClient<IOrderExecutor, AlpacaPaperOrderExecutor>(c => c.Timeout = timeout)
        .AddTypedClient((http, _) => (IOrderExecutor)new AlpacaPaperOrderExecutor(
            http, options.AlpacaApiKeyId, options.AlpacaApiSecretKey, options.AlpacaTradingBaseUrl));
}
else
{
    builder.Services.AddScoped<IOrderExecutor, RefusingOrderExecutor>();
}

builder.Services.AddScoped<TradingCoordinator>();
builder.Services.AddHostedService<TradingWorker>();

var app = builder.Build();

// ── Health and metrics ───────────────────────────────────────────────────
// Liveness: the process is running. Deliberately independent of the broker —
// Alpaca being unreachable is not a reason to restart this pod.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Readiness: a full evaluation cycle has completed recently. A pod that
// cannot see the market should not be reporting itself fit.
app.MapGet("/readyz", (AgentState state) =>
    state.IsReady
        ? Results.Ok(state.Snapshot())
        : Results.Json(state.Snapshot(), statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapGet("/", (AgentState state, AgentOptions o, TradingPolicySet p) => Results.Ok(new
{
    service = "trading-agent",
    mode = o.TradingMode,
    tradingEnabled = p.Risk.TradingEnabled,
    strategy = p.StrategyName,
    symbols = p.Allowlist.OrderBy(s => s, StringComparer.Ordinal),
    state = state.Snapshot(),
}));

app.MapMetrics();   // /metrics

return await RunAsync(app);

static async Task<int> RunAsync(WebApplication app)
{
    await app.RunAsync();
    return 0;
}
