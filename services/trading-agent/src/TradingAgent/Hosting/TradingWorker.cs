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
/// The evaluation loop of a day-trading agent.
///
/// One pass per interval, in a fixed order that matters:
///
///   1. exits  — every open position is checked against its stop, its target
///               and the session's flatten deadline
///   2. entries — only if the clock is inside the entry window
///
/// Exits run first and run unconditionally. An agent that is barred from
/// entering must still be able to leave, and the deadline that makes this a
/// day-trading system rather than a swing-trading one is enforced here: past
/// the flatten time every position is closed, profitable or not.
///
/// Every failure path ends in no new risk. Stale data, a missing price, an
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
    private static readonly Counter Exits = Metrics.CreateCounter(
        "trading_agent_exits_total", "Positions closed, by the rule that closed them.", new CounterConfiguration { LabelNames = ["symbol", "reason"] });
    private static readonly Histogram CycleDuration = Metrics.CreateHistogram(
        "trading_agent_cycle_duration_seconds", "Duration of an evaluation cycle.");
    private static readonly Gauge MarketOpen = Metrics.CreateGauge(
        "trading_agent_market_open", "1 when the market is open, 0 when closed.");
    private static readonly Gauge TradingEnabled = Metrics.CreateGauge(
        "trading_agent_trading_enabled", "1 when order submission is permitted.");
    private static readonly Gauge EntryWindowOpen = Metrics.CreateGauge(
        "trading_agent_entry_window_open", "1 when the session clock permits new entries.");
    private static readonly Gauge SessionMinutesRemaining = Metrics.CreateGauge(
        "trading_agent_session_minutes_remaining", "Minutes until the regular session closes.");
    private static readonly Gauge OpenPositions = Metrics.CreateGauge(
        "trading_agent_open_positions", "Positions currently held.");
    private static readonly Gauge DayTradesUsed = Metrics.CreateGauge(
        "trading_agent_day_trades_used", "Day trades used in the broker's rolling five-day window.");
    private static readonly Counter DataRejections = Metrics.CreateCounter(
        "trading_agent_market_data_rejections_total",
        "Evaluations abandoned because the quote was not tradable, by reason. A high wide_spread "
        + "rate on liquid symbols indicates a thin data feed rather than a wide market.",
        new CounterConfiguration { LabelNames = ["symbol", "reason"] });
    private static readonly Counter AuditFailures = Metrics.CreateCounter(
        "trading_agent_audit_failures_total", "Decisions that could not be persisted.");

    private static readonly string PodName = Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.EvaluationIntervalSeconds);
        TradingEnabled.Set(policies.Risk.TradingEnabled ? 1 : 0);

        logger.LogInformation(
            "Day-trading agent started. mode={Mode} tradingEnabled={TradingEnabled} symbols={SymbolCount} "
            + "interval={IntervalSeconds}s dataFeed={DataFeed} stop={StopPercent}% target={TargetPercent}% "
            + "flatten={FlattenMinutes}m before the close",
            options.TradingMode, policies.Risk.TradingEnabled, policies.Allowlist.Count,
            options.EvaluationIntervalSeconds, options.NormalisedDataFeed,
            policies.Exits.StopLossPercent, policies.Exits.TakeProfitPercent,
            policies.Session.FlattenBeforeClose.TotalMinutes);

        if (options.NormalisedDataFeed == AgentOptions.DefaultFeed)
        {
            logger.LogWarning(
                "Quoting from the free '{Feed}' feed, which carries a small share of US equity volume. "
                + "Expect entries to be suppressed on symbols that trade thinly on it — watch "
                + "trading_agent_market_data_rejections_total{{reason=\"wide_spread\"}}. Set ALPACA_DATA_FEED=sip "
                + "(paid) before drawing conclusions about whether a strategy works.",
                options.NormalisedDataFeed);
        }

        if (!policies.Risk.TradingEnabled)
        {
            logger.LogWarning(
                "Order submission is DISABLED. The agent will evaluate and log decisions without placing any order. "
                + "Note that this also disables the end-of-day flatten.");
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

        logger.LogInformation("Day-trading agent stopped.");
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
            EntryWindowOpen.Set(0);
            SessionMinutesRemaining.Set(0);
            await ReportPositionsHeldWhileClosedAsync(accounts, clock, cancellationToken);
            state.RecordCycleSuccess(0, "market closed");
            CycleDuration.Observe(stopwatch.Elapsed.TotalSeconds);
            return;
        }

        var session = await market.GetSessionAsync(clock, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        SessionMinutesRemaining.Set(Math.Max(0, session.Remaining(now).TotalMinutes));

        var account = await accounts.GetAsync(cancellationToken);
        var ordersToday = await accounts.GetTodaysOrdersAsync(cancellationToken);

        OpenPositions.Set(account.OpenPositionCount);
        if (account.DayTradeCount is { } dayTrades) DayTradesUsed.Set(dayTrades);

        var openOrderSymbols = ordersToday
            .Where(o => o.Status is "new" or "accepted" or "partially_filled" or "pending_new")
            .Select(o => o.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── 1. Exits ─────────────────────────────────────────────────────
        var closing = await RunExitPassAsync(
            account, ordersToday, openOrderSymbols, session, clock, coordinator, store, now, cancellationToken);

        // ── 2. Entries ───────────────────────────────────────────────────
        var window = SessionWindow.EvaluateEntry(session, policies.Session, now);
        EntryWindowOpen.Set(window.IsOpen ? 1 : 0);

        if (!window.IsOpen)
        {
            logger.LogInformation(
                "No new entries: {Reason} ({Code}) minutesToClose={MinutesToClose:0}",
                window.Reason, window.Code, session.Remaining(now).TotalMinutes);
            state.RecordCycleSuccess(0, $"exits only — {window.Code.ToLowerInvariant()}");
            CycleDuration.Observe(stopwatch.Elapsed.TotalSeconds);
            return;
        }

        var evaluated = 0;
        foreach (var symbol in policies.Allowlist.OrderBy(s => s, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A symbol whose position is being closed this cycle must not be
            // re-entered on a snapshot that predates the liquidation.
            if (closing.Contains(symbol))
            {
                Evaluations.WithLabels(symbol, "closing").Inc();
                evaluated++;
                continue;
            }

            var outcome = await EvaluateSymbolAsync(
                symbol, clock, account, ordersToday, openOrderSymbols, market, strategy, coordinator, store, cancellationToken);
            Evaluations.WithLabels(symbol, outcome).Inc();
            evaluated++;
        }

        state.RecordCycleSuccess(evaluated, "evaluated");
        CycleDuration.Observe(stopwatch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Checks every open position against the stop, the target and the
    /// flatten deadline, and closes the ones that have hit a rule.
    /// Returns the symbols a close was submitted for.
    /// </summary>
    private async Task<IReadOnlySet<string>> RunExitPassAsync(
        AccountSnapshotProvider.Snapshot account,
        IReadOnlyList<AccountSnapshotProvider.OrderSnapshot> ordersToday,
        IReadOnlySet<string> openOrderSymbols,
        TradingSession session,
        MarketClock clock,
        TradingCoordinator coordinator,
        IDecisionStore store,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var closing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (account.Positions.Count == 0) return closing;

        var openedAt = DerivePositionOpenTimes(ordersToday);

        foreach (var raw in account.Positions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var position = openedAt.TryGetValue(raw.Symbol, out var opened)
                ? raw with { OpenedAtUtc = opened }
                : raw;

            if (!position.PnlKnown)
            {
                // Visible rather than silent: the flatten deadline still
                // applies, but this position has no working stop or target
                // until the broker reports its P&L again.
                logger.LogWarning(
                    "{Symbol}: the broker did not report unrealised P&L. The stop and target are suspended "
                    + "for this position; the end-of-day flatten still applies.", position.Symbol);
            }

            var exit = ExitManager.Evaluate(position, policies.Exits, policies.Session, session, now);
            if (!exit.ShouldExit) continue;

            // The exit is expressed as a normal SELL proposal so that it is
            // audited in the same shape as everything else, and so that it
            // still has to pass the risk engine.
            var proposal = new StrategySignal(
                position.Symbol,
                TradeAction.Sell,
                position.MarketValue,
                1.0m,
                "exit-manager",
                exit.Explanation,
                now);

            var accountState = new AccountRiskState(
                Cash: account.Cash,
                PortfolioExposure: account.PortfolioExposure,
                DailyRealizedPnl: account.DayPnl,
                OpenPositionCount: account.OpenPositionCount,
                TotalOrdersToday: ordersToday.Count,
                OrdersForSymbolToday: ordersToday.Count(o => string.Equals(o.Symbol, position.Symbol, StringComparison.OrdinalIgnoreCase)),
                MarketOpen: true,
                IsPaperEndpoint: true,
                HasOpenOrderForSymbol: openOrderSymbols.Contains(position.Symbol),
                ExistingPositionNotional: position.MarketValue,
                Equity: account.Equity,
                DayTradeCount: account.DayTradeCount);

            TradingRunResult result;
            try
            {
                result = await coordinator.ProcessAsync(
                    proposal, accountState, policies.Risk, policies.Allowlist, now,
                    OrderIntent.Exit, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed close is the most important thing this agent can
                // report: the position is still open and the deadline is not
                // going to wait.
                logger.LogError(ex,
                    "COULD NOT CLOSE {Symbol} ({Reason}). The position is still open.",
                    position.Symbol, exit.Reason);
                continue;
            }

            await PersistAsync(store, DecisionRecord.From(
                proposal, result.Decision, result.BrokerOrder,
                policies.Risk.TradingEnabled, clock.IsOpen, PodName), cancellationToken);

            if (result.Submitted)
            {
                closing.Add(position.Symbol);
                Exits.WithLabels(position.Symbol, ExitReasonLabel(exit.Reason)).Inc();
                logger.LogWarning(
                    "CLOSING {Symbol} {Reason}: {Explanation} unrealisedPnl={Pnl} ({PnlPct}%) brokerStatus={BrokerStatus}",
                    position.Symbol, exit.Reason, exit.Explanation,
                    Format(position.UnrealizedPnl), Format(position.UnrealizedPnlFraction * 100m),
                    result.BrokerOrder?.Status);
            }
            else
            {
                // Rejected exits are logged at Warning, not Information: an
                // exit the risk engine refuses is either transient
                // (DUPLICATE_EXPOSURE, a close already working) or a defect,
                // and the difference matters near the close.
                logger.LogWarning(
                    "EXIT REJECTED {Symbol} ({Reason}): {Code} — {Message}",
                    position.Symbol, exit.Reason, result.Code, result.Message);
            }
        }

        return closing;
    }

    /// <summary>
    /// When each currently open position was opened, taken from the broker's
    /// own fills: the earliest buy that is not cancelled out by a later sell.
    /// A symbol with no qualifying fill today simply has no max-hold timer —
    /// the flatten deadline still applies — because a guessed entry time
    /// would produce a guessed exit.
    /// </summary>
    private static Dictionary<string, DateTimeOffset> DerivePositionOpenTimes(
        IReadOnlyList<AccountSnapshotProvider.OrderSnapshot> ordersToday)
    {
        var filled = ordersToday.Where(o => o.FilledAtUtc is not null).ToList();
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in filled.GroupBy(o => o.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            var lastSell = group
                .Where(o => string.Equals(o.Side, "sell", StringComparison.OrdinalIgnoreCase))
                .Select(o => o.FilledAtUtc!.Value)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();

            var firstBuyAfter = group
                .Where(o => string.Equals(o.Side, "buy", StringComparison.OrdinalIgnoreCase)
                            && o.FilledAtUtc!.Value > lastSell)
                .Select(o => o.FilledAtUtc!.Value)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Min();

            if (firstBuyAfter > DateTimeOffset.MinValue) result[group.Key] = firstBuyAfter;
        }

        return result;
    }

    /// <summary>
    /// A position that is still open while the market is closed means a
    /// flatten did not happen — the agent was down, trading was disabled, or
    /// a close was rejected. Nothing can be done about it now, but an
    /// operator needs to know the account is carrying overnight risk.
    /// </summary>
    private async Task ReportPositionsHeldWhileClosedAsync(
        AccountSnapshotProvider accounts, MarketClock clock, CancellationToken cancellationToken)
    {
        try
        {
            var account = await accounts.GetAsync(cancellationToken);
            OpenPositions.Set(account.OpenPositionCount);
            if (account.DayTradeCount is { } dayTrades) DayTradesUsed.Set(dayTrades);

            if (account.OpenPositionCount == 0)
            {
                logger.LogInformation(
                    "Market is closed and the account is flat. nextOpen={NextOpenUtc:o}", clock.NextOpenUtc);
                return;
            }

            logger.LogWarning(
                "Market is closed and {Count} position(s) are still open: {Symbols}. "
                + "A day-trading account should be flat overnight — check why the flatten did not complete.",
                account.OpenPositionCount,
                string.Join(", ", account.Positions.Select(p => p.Symbol).OrderBy(s => s, StringComparer.Ordinal)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Market is closed; could not read the account to confirm the book is flat.");
        }
    }

    private async Task<string> EvaluateSymbolAsync(
        string symbol,
        MarketClock clock,
        AccountSnapshotProvider.Snapshot account,
        IReadOnlyList<AccountSnapshotProvider.OrderSnapshot> ordersToday,
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
            var reason = ex is MarketDataException typed
                ? RejectionLabel(typed.Reason)
                : RejectionLabel(MarketDataRejection.Unavailable);
            DataRejections.WithLabels(symbol, reason).Inc();

            logger.LogInformation(
                "HOLD {Symbol}: market data unusable ({Reason}) — {Detail}", symbol, reason, ex.Message);
            await PersistAsync(store, DecisionRecord.NoData(
                symbol, ex.Message, policies.Risk.TradingEnabled, clock.IsOpen, PodName), cancellationToken);
            return "no_data";
        }

        var proposal = strategy.Evaluate(inputs, policies.Strategy, now);

        var accountState = new AccountRiskState(
            Cash: account.Cash,
            PortfolioExposure: account.PortfolioExposure,
            // Real day P&L from the broker. This was previously hardcoded to
            // zero, which silently disabled the daily loss limit — the risk
            // engine's check is `DailyRealizedPnl <= -MaxDailyRealizedLoss`,
            // and zero never satisfies it.
            DailyRealizedPnl: account.DayPnl,
            OpenPositionCount: account.OpenPositionCount,
            TotalOrdersToday: ordersToday.Count,
            OrdersForSymbolToday: ordersToday.Count(o => string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase)),
            MarketOpen: clock.IsOpen,
            // Startup refuses any endpoint other than the paper host over
            // HTTPS, so by the time a cycle runs this is established fact.
            IsPaperEndpoint: true,
            HasOpenOrderForSymbol: openOrderSymbols.Contains(symbol),
            ExistingPositionNotional: account.PositionNotionalBySymbol.GetValueOrDefault(symbol, 0m),
            Equity: account.Equity,
            DayTradeCount: account.DayTradeCount);

        var result = await coordinator.ProcessAsync(
            proposal, accountState, policies.Risk, policies.Allowlist, now,
            OrderIntent.Entry, cancellationToken);

        await PersistAsync(store, DecisionRecord.From(
            proposal, result.Decision, result.BrokerOrder,
            policies.Risk.TradingEnabled, clock.IsOpen, PodName), cancellationToken);

        if (result.Submitted)
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

    private static string RejectionLabel(MarketDataRejection reason) => reason switch
    {
        MarketDataRejection.NonPositivePrice => "non_positive_price",
        MarketDataRejection.CrossedQuote => "crossed_quote",
        MarketDataRejection.Stale => "stale",
        MarketDataRejection.WideSpread => "wide_spread",
        _ => "unavailable",
    };

    private static string Format(decimal? value) =>
        value?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";

    private static string ExitReasonLabel(ExitReason reason) => reason switch
    {
        ExitReason.SessionClose => "session_close",
        ExitReason.StopLoss => "stop_loss",
        ExitReason.TakeProfit => "take_profit",
        ExitReason.MaxHoldTime => "max_hold_time",
        _ => "none",
    };

    /// <summary>
    /// Persist a decision without letting a storage failure stop trading
    /// evaluation. The failure is counted and logged so it is visible rather
    /// than silent.
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
