using ClaudeTradingAgent.Charting;
using ClaudeTradingAgent.Execution;
using ClaudeTradingAgent.MarketData;
using ClaudeTradingAgent.Persistence;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;
using ClaudeTradingAgent.TechnicalAnalysis;
using ClaudeTradingAgent.TradingAgent;
using ClaudeTradingAgent.TradingAgent.Configuration;
using ClaudeTradingAgent.TradingAgent.Hosting;
using ClaudeTradingAgent.TradingAgent.Observability;
using Microsoft.Extensions.Logging.Console;
using Npgsql;
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
    AlpacaDataFeed = builder.Configuration["ALPACA_DATA_FEED"] ?? AgentOptions.DefaultFeed,
    AlpacaApiKeyId = builder.Configuration["ALPACA_API_KEY_ID"] ?? string.Empty,
    AlpacaApiSecretKey = builder.Configuration["ALPACA_API_SECRET_KEY"] ?? string.Empty,
    TradingConfigPath = builder.Configuration["TRADING_CONFIG_PATH"] ?? "config/trading.json",
    SymbolConfigPath = builder.Configuration["SYMBOL_CONFIG_PATH"] ?? "config/symbols.json",
    EvaluationIntervalSeconds = builder.Configuration.GetValue("EVALUATION_INTERVAL_SECONDS", 60),
    BrokerTimeoutSeconds = builder.Configuration.GetValue("BROKER_TIMEOUT_SECONDS", 10),
    DatabaseConnectionString = builder.Configuration.GetConnectionString("Default") ?? string.Empty,
};

var configErrors = options.Validate();
if (configErrors.Count > 0)
{
    // Fail fast and loudly. A trading agent running on configuration it does
    // not understand is worse than one that refuses to start.
    foreach (var error in configErrors) Console.Error.WriteLine($"CONFIG ERROR: {error}");
    return 1;
}

TradingPolicySet policies;
try
{
    policies = TradingPolicySet.Load(options);
}
catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"CONFIG ERROR: {ex.Message}");
    return 1;
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(policies);
builder.Services.AddSingleton<AgentState>();
builder.Services.AddSingleton<MomentumStrategy>();

// ── Decision audit ───────────────────────────────────────────────────────
// Every evaluation is recorded, including the ones that produced no trade.
// Without a connection string the agent still runs; it simply keeps no
// history beyond the log retention window.
if (options.HasDatabase)
{
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(options.DatabaseConnectionString));
    builder.Services.AddSingleton<IDecisionStore, PostgresDecisionStore>();
}
else
{
    builder.Services.AddSingleton<IDecisionStore, NullDecisionStore>();
}
builder.Services.AddSingleton<RiskEngine>();

// ── Broker clients ───────────────────────────────────────────────────────
var timeout = TimeSpan.FromSeconds(options.BrokerTimeoutSeconds);

builder.Services.AddHttpClient<IMarketDataProvider, AlpacaMarketDataProvider>(c => c.Timeout = timeout)
    .AddTypedClient((http, _) => (IMarketDataProvider)new AlpacaMarketDataProvider(
        http, options.AlpacaApiKeyId, options.AlpacaApiSecretKey,
        options.AlpacaDataBaseUrl, options.AlpacaTradingBaseUrl, options.NormalisedDataFeed));

builder.Services.AddHttpClient<AccountSnapshotProvider>(c => c.Timeout = timeout)
    .AddTypedClient((http, _) => new AccountSnapshotProvider(
        http, options.AlpacaApiKeyId, options.AlpacaApiSecretKey, options.AlpacaTradingBaseUrl));

// The executor is chosen by configuration, not by a runtime branch inside
// the trading path: when trading is disabled there is no code path that can
// reach a broker at all.
if (policies.Risk.TradingEnabled)
{
    builder.Services.AddHttpClient<AlpacaPaperOrderExecutor>(c => c.Timeout = timeout)
        .AddTypedClient((http, _) => new AlpacaPaperOrderExecutor(
            http, options.AlpacaApiKeyId, options.AlpacaApiSecretKey, options.AlpacaTradingBaseUrl));

    // Every order goes through the auditor first. It refuses to submit
    // anything it cannot record.
    builder.Services.AddScoped<IOrderExecutor>(sp => new AuditingOrderExecutor(
        sp.GetRequiredService<AlpacaPaperOrderExecutor>(),
        sp.GetRequiredService<IDecisionStore>(),
        sp.GetRequiredService<ILogger<AuditingOrderExecutor>>()));
}
else
{
    builder.Services.AddScoped<IOrderExecutor, RefusingOrderExecutor>();
}

builder.Services.AddScoped<TradingCoordinator>();

// ── Technical analysis (phase 1: read-only) ──────────────────────────────
// Charts and analysis for operators and later phases. Nothing here reaches
// the trading loop: no order, stop or risk decision uses it yet.
builder.Services.AddSingleton(TechnicalAnalysisSettings.Default);
builder.Services.AddTransient<ICandleSource, MarketDataCandleSource>();
builder.Services.AddTransient<ChartDataBuilder>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
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
    style = "day-trading",
    mode = o.TradingMode,
    tradingEnabled = p.Risk.TradingEnabled,
    auditEnabled = o.HasDatabase,
    strategy = p.StrategyName,
    dataFeed = o.NormalisedDataFeed,
    symbols = p.Allowlist.OrderBy(s => s, StringComparer.Ordinal),
    // The rules that make this a day-trading agent, visible without reading
    // the config that is baked into the image.
    session = new
    {
        // New York clock times on a regular day; on an early close each
        // boundary keeps its distance from the real close.
        entryStartEt = p.Session.EntryStartEt.ToString("HH:mm"),
        entryCutoffEt = p.Session.EntryCutoffEt.ToString("HH:mm"),
        flattenEt = p.Session.FlattenEt.ToString("HH:mm"),
    },
    capital = new
    {
        strategyCapital = p.Risk.StrategyCapital,
        maxNotionalPerTrade = p.Risk.MaxNotionalPerTrade,
        maxConcurrentPositions = p.Risk.MaxConcurrentPositions,
        maxTotalExposure = p.Risk.MaxTotalExposure,
        maxDailyLoss = p.Risk.MaxDailyLoss,
        maxEstimatedLossPerTrade = p.Risk.MaxEstimatedLossPerTrade,
    },
    exits = new
    {
        stopLossPercent = p.Exits.StopLossPercent,
        takeProfitPercent = p.Exits.TakeProfitPercent,
        maxHoldMinutes = p.Exits.MaxHoldTime.TotalMinutes,
    },
    state = state.Snapshot(),
}));

// Chart-ready candles, overlays, swings, levels and the analysis behind them.
//   GET /charts/AAPL?timeframe=5m&start=2026-09-14T13:30:00Z&end=2026-09-14T20:00:00Z
app.MapGet("/charts/{symbol}", async (
    string symbol, string? timeframe, DateTimeOffset? start, DateTimeOffset? end,
    TradingPolicySet p, ChartDataBuilder charts, CancellationToken ct) =>
{
    if (!p.Allowlist.Contains(symbol)) return Results.NotFound(new { error = $"{symbol} is not an allowlisted symbol." });
    if (!TimeframeExtensions.TryParse(timeframe ?? "5m", out var tf)) return Results.BadRequest(new { error = "timeframe must be 1m, 5m or 15m." });
    var endUtc = (end ?? DateTimeOffset.UtcNow).ToUniversalTime();
    var startUtc = (start ?? endUtc.AddDays(-1)).ToUniversalTime();
    if (endUtc <= startUtc || endUtc - startUtc > TimeSpan.FromDays(7)) return Results.BadRequest(new { error = "start must be before end, and the range at most 7 days." });
    try
    {
        return Results.Ok(await charts.BuildAsync(symbol.ToUpperInvariant(), tf, startUtc, endUtc, cancellationToken: ct));
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

// 15m / 5m / 1m trend, structure, swings, levels and breakout, as of now or ?asOf=.
app.MapGet("/analysis/{symbol}", async (
    string symbol, DateTimeOffset? asOf, TradingPolicySet p, ICandleSource candles,
    TechnicalAnalysisSettings settings, CancellationToken ct) =>
{
    if (!p.Allowlist.Contains(symbol)) return Results.NotFound(new { error = $"{symbol} is not an allowlisted symbol." });
    var asOfUtc = (asOf ?? DateTimeOffset.UtcNow).ToUniversalTime();
    try
    {
        var oneMinute = await candles.GetOneMinuteCandlesAsync(
            symbol.ToUpperInvariant(), asOfUtc - ChartDataBuilder.WarmUp(Timeframe.FifteenMinutes, settings), asOfUtc, ct);
        return Results.Ok(new MultiTimeframeAnalyzer(settings).Analyze(oneMinute, asOfUtc));
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapMetrics();   // /metrics

// Create the audit schema before the worker starts writing to it.
using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<IDecisionStore>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    try
    {
        await store.InitialiseAsync();
    }
    catch (Exception ex)
    {
        // Do not block startup: with trading disabled, losing the audit
        // trail is a degraded state rather than an unsafe one. Enabling
        // trading changes that calculus — see the note in AgentOptions.
        startupLogger.LogError(ex, "Could not prepare the decision audit store; decisions will not be persisted.");
    }
}

return await RunAsync(app);

static async Task<int> RunAsync(WebApplication app)
{
    await app.RunAsync();
    return 0;
}
