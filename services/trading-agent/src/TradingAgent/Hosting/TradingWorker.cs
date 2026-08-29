using System.Diagnostics;
using ClaudeTradingAgent.MarketData;
using ClaudeTradingAgent.Persistence;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;
using ClaudeTradingAgent.TradingAgent.Configuration;
using ClaudeTradingAgent.TradingAgent.Observability;
using Prometheus;

namespace ClaudeTradingAgent.TradingAgent.Hosting;

/// <summary>
/// The evaluation loop.
///
/// One pass per interval: read the clock, and for each allowlisted symbol
/// gather market data, ask the strategy for a proposal, and put that
/// proposal through the deterministic risk engine. Nothing reaches the
/// broker unless the risk engine approves it, and with trading disabled it
/// never will.
///
/// Every failure path ends in no trade. Stale data, a missing price, an
/// unreachable broker and a malformed response all produce a logged HOLD
/// rather than an assumption.
/// </summary>
public sealed class TradingWorker(
    ILogger<TradingWorker> logger,
    AgentOptions options,
    TradingPolicySet policies,
    AgentState state,
    IServiceProvider services) : BackgroundService
{
    private static readonly Counter Evaluations = Metrics.CreateCounter(
        "trading_agent_evaluations_total", "Symbol evaluations completed.", new CounterConfiguration { LabelNames = ["symbol", "outcome"] });
    private static readonly Counter Cycles = Metrics.CreateCounter(
        "trading_agent_cycles_total", "Evaluation cycles completed.", new CounterConfiguration { LabelNames = ["result"] });
    private static readonly Histogram CycleDuration = Metrics.CreateHistogram(
        "trading_agent_cycle_duration_seconds", "Duration of an evaluation cycle.");
    private static readonly Gauge MarketOpen = Metrics.CreateGauge(
        "trading_agent_market_open", "1 when the market is open, 0 when closed.");
    private static readonly Gauge TradingEnabled = Metrics.CreateGauge(
        "trading_agent_trading_enabled", "1 when order submission is permitted.");
    private static readonly Counter AuditFailures = Metrics.CreateCounter(
        "trading_agent_audit_failures_total", "Decisions that could not be persisted.");

    private static readonly string PodName = Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.EvaluationIntervalSeconds);
        TradingEnabled.Set(policies.Risk.TradingEnabled ? 1 : 0);

        logger.LogInformation(
            "Trading agent started. mode={Mode} tradingEnabled={TradingEnabled} symbols={SymbolCount} interval={IntervalSeconds}s",
            options.TradingMode, policies.Risk.TradingEnabled, policies.Allowlist.Count, options.EvaluationIntervalSeconds);

        if (!policies.Risk.TradingEnabled)
        {
            logger.LogWarning(
                "Order submission is DISABLED. The agent will evaluate and log decisions without placing any order.");
        }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await RunCycleAsync(stoppingToken);
                Cycles.WithLabels("ok").Inc();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed cycle must never kill the loop: the next one may
                // succeed, and a crash-looping agent tells an operator less
                // than a running one reporting failures.
                Cycles.WithLabels("failed").Inc();
                state.RecordCycleFailure(ex.Message);
                logger.LogError(ex, "Evaluation cycle failed; no orders were placed.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("Trading agent stopped.");
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var scope = services.CreateScope();
        var market = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountSnapshotProvider>();
        var strategy = scope.ServiceProvider.GetRequiredService<MomentumStrategy>();
        var coordinator = scope.ServiceProvider.GetRequiredService<TradingCoordinator>();
        var store = scope.ServiceProvider.GetRequiredService<IDecisionStore>();

        var clock = await market.GetMarketClockAsync(cancellationToken);
        MarketOpen.Set(clock.IsOpen ? 1 : 0);

        if (!clock.IsOpen)
        {
            logger.LogInformation(
                "Market is closed; skipping evaluation. nextOpen={NextOpenUtc:o}", clock.NextOpenUtc);
            state.RecordCycleSuccess(0, "market closed");
            CycleDuration.Observe(stopwatch.Elapsed.TotalSeconds);
            return;
        }

        var account = await accounts.GetAsync(cancellationToken);
        var ordersToday = await accounts.GetTodaysOrdersAsync(cancellationToken);
        var openOrderSymbols = ordersToday
            .Where(o => o.Status is "new" or "accepted" or "partially_filled" or "pending_new")
            .Select(o => o.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var evaluated = 0;
        foreach (var symbol in policies.Allowlist.OrderBy(s => s, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await EvaluateSymbolAsync(
                symbol, clock, account, ordersToday, openOrderSymbols, market, strategy, coordinator, store, cancellationToken);
            Evaluations.WithLabels(symbol, outcome).Inc();
            evaluated++;
        }

        state.RecordCycleSuccess(evaluated, "evaluated");
        CycleDuration.Observe(stopwatch.Elapsed.TotalSeconds);
    }

    private async Task<string> EvaluateSymbolAsync(
        string symbol,
        MarketClock clock,
        AccountSnapshotProvider.Snapshot account,
        IReadOnlyList<(string Symbol, string Status)> ordersToday,
        IReadOnlySet<string> openOrderSymbols,
        IMarketDataProvider market,
        MomentumStrategy strategy,
        TradingCoordinator coordinator,
        IDecisionStore store,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        MomentumInputs inputs;
        try
        {
            var quote = await market.GetLatestQuoteAsync(symbol, cancellationToken);
            MarketDataValidator.ValidateQuote(quote, now, policies.Strategy.MaxDataAge, policies.Strategy.MaximumSpreadBps);

            var bars = await market.GetRecentBarsAsync(symbol, policies.LookbackBars, cancellationToken);
            inputs = BuildInputs(symbol, quote, bars);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            // Fail closed. Never infer a price, a spread or a volume.
            logger.LogInformation(
                "HOLD {Symbol}: market data unusable — {Reason}", symbol, ex.Message);
            await PersistAsync(store, DecisionRecord.NoData(
                symbol, ex.Message, policies.Risk.TradingEnabled, clock.IsOpen, PodName), cancellationToken);
            return "no_data";
        }

        var proposal = strategy.Evaluate(inputs, policies.Strategy, now);

        var accountState = new AccountRiskState(
            Cash: account.Cash,
            PortfolioExposure: account.PortfolioExposure,
            DailyRealizedPnl: 0m,
            OpenPositionCount: account.OpenPositionCount,
            TotalOrdersToday: ordersToday.Count,
            OrdersForSymbolToday: ordersToday.Count(o => string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase)),
            MarketOpen: clock.IsOpen,
            IsPaperEndpoint: true,
            HasOpenOrderForSymbol: openOrderSymbols.Contains(symbol),
            ExistingPositionNotional: account.PositionNotionalBySymbol.GetValueOrDefault(symbol, 0m));

        var result = await coordinator.ProcessAsync(
            proposal, accountState, policies.Risk, policies.Allowlist, now, cancellationToken);

        await PersistAsync(store, DecisionRecord.From(
            proposal,
            new RiskManagement.RiskDecision(result.Status == "SUBMITTED", result.Code, result.Message,
                                            result.BrokerOrder is null ? null : new RiskManagement.ApprovedOrder(
                                                result.BrokerOrder.ClientOrderId, proposal.Symbol,
                                                proposal.Action, proposal.ProposedNotional, now)),
            result.BrokerOrder,
            policies.Risk.TradingEnabled,
            clock.IsOpen,
            PodName), cancellationToken);

        if (result.Status == "SUBMITTED")
        {
            logger.LogWarning(
                "ORDER SUBMITTED {Symbol} {Action} notional={Notional} clientOrderId={ClientOrderId} brokerStatus={BrokerStatus}",
                symbol, proposal.Action, proposal.ProposedNotional,
                result.BrokerOrder?.ClientOrderId, result.BrokerOrder?.Status);
            return "submitted";
        }

        logger.LogInformation(
            "{Decision} {Symbol}: {Code} — {Message} (action={Action} confidence={Confidence:0.00})",
            result.Status, symbol, result.Code, result.Message, proposal.Action, proposal.Confidence);

        return result.Code.ToLowerInvariant();
    }

    /// <summary>
    /// Persist a decision without letting a storage failure stop trading
    /// evaluation. The failure is counted and logged so it is visible rather
    /// than silent; with trading disabled, losing an audit row is degraded
    /// rather than unsafe.
    /// </summary>
    private async Task PersistAsync(IDecisionStore store, DecisionRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await store.RecordAsync(record, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AuditFailures.Inc();
            logger.LogError(ex, "Could not persist the decision for {Symbol}.", record.Symbol);
        }
    }

    private static MomentumInputs BuildInputs(string symbol, QuoteSnapshot quote, IReadOnlyList<Bar> bars)
    {
        var closes = bars.Select(b => b.Close).ToArray();
        var fastWindow = Math.Min(5, closes.Length);
        var slowWindow = closes.Length;

        var fast = closes.TakeLast(fastWindow).Average();
        var slow = closes.Average();

        var volumes = bars.Select(b => (decimal)b.Volume).ToArray();
        var recentVolume = volumes.TakeLast(fastWindow).Average();
        var averageVolume = volumes.Average();
        var volumeRatio = averageVolume <= 0 ? 0m : recentVolume / averageVolume;

        return new MomentumInputs(
            symbol.ToUpperInvariant(),
            quote.Mid,
            fast,
            slow,
            volumeRatio,
            quote.SpreadBps,
            quote.TimestampUtc);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }
}
