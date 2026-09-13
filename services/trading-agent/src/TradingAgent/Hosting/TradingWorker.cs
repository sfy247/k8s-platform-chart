using System.Diagnostics;
using ClaudeTradingAgent.Execution;
using ClaudeTradingAgent.MarketData;
using ClaudeTradingAgent.Persistence;
using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;
using ClaudeTradingAgent.TradingAgent.Configuration;
using ClaudeTradingAgent.TradingAgent.Observability;
using Prometheus;

namespace ClaudeTradingAgent.TradingAgent.Hosting;

/// <summary>
/// The evaluation loop of the v3 day-trading agent. Each cycle:
///
///   1. resolve the session state   MARKET_CLOSED / PRE_MARKET_DISABLED /
///                                  ENTRY_WINDOW / MANAGEMENT_ONLY / FLATTEN_WINDOW
///   2. reconcile                   unclear orders and final order outcomes
///   3. exits                       stop, target, max hold, flatten — always
///   4. entries                     only in ENTRY_WINDOW, with no lockout and
///                                  no unreconciled order
///
/// Every failure path ends in no new risk. Exits run before entries and do
/// not depend on anything that can block an entry.
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
    private static readonly Counter DataRejections = Metrics.CreateCounter(
        "trading_agent_market_data_rejections_total",
        "Evaluations abandoned because the quote was not tradable, by reason.",
        new CounterConfiguration { LabelNames = ["symbol", "reason"] });
    private static readonly Histogram CycleDuration = Metrics.CreateHistogram(
        "trading_agent_cycle_duration_seconds", "Duration of an evaluation cycle.");
    private static readonly Gauge MarketOpen = Metrics.CreateGauge(
        "trading_agent_market_open", "1 when the market is open, 0 when closed.");
    private static readonly Gauge TradingEnabled = Metrics.CreateGauge(
        "trading_agent_trading_enabled", "1 when order submission is permitted.");
    private static readonly Gauge EntryWindowOpen = Metrics.CreateGauge(
        "trading_agent_entry_window_open", "1 when the session state is ENTRY_WINDOW.");
    private static readonly Gauge SessionStateGauge = Metrics.CreateGauge(
        "trading_agent_session_state", "1 for the current session state, 0 for the others.", new GaugeConfiguration { LabelNames = ["state"] });
    private static readonly Gauge SessionMinutesRemaining = Metrics.CreateGauge(
        "trading_agent_session_minutes_remaining", "Minutes until the regular session closes.");
    private static readonly Gauge OpenPositions = Metrics.CreateGauge(
        "trading_agent_open_positions", "Positions currently held.");
    private static readonly Gauge DayTradesUsed = Metrics.CreateGauge(
        "trading_agent_day_trades_used", "Day trades used in the broker's rolling five-day window.");
    private static readonly Gauge DailyLockout = Metrics.CreateGauge(
        "trading_agent_daily_loss_lockout", "1 when the daily loss limit has blocked entries for the rest of the session.");
    private static readonly Gauge UncertainOrders = Metrics.CreateGauge(
        "trading_agent_uncertain_orders", "Orders whose broker status is unconfirmed. Any value above 0 blocks new entries.");
    private static readonly Counter AuditFailures = Metrics.CreateCounter(
        "trading_agent_audit_failures_total", "Decisions that could not be persisted.");

    private static readonly string PodName = Environment.MachineName;

    private static readonly string[] OpenOrderStatuses =
        ["new", "accepted", "partially_filled", "pending_new", "pending_cancel", "pending_replace", "accepted_for_bidding", "calculated", "held"];

    /// <summary>Everything one cycle knows, so the passes do not take a dozen parameters each.</summary>
    private sealed record Cycle(
        MarketClock Clock,
        SessionSchedule Schedule,
        SessionState SessionState,
        AccountSnapshotProvider.Snapshot Account,
        IReadOnlyList<AccountSnapshotProvider.OrderSnapshot> OrdersToday,
        IReadOnlySet<string> OpenOrderSymbols,
        bool Lockout,
        bool Uncertain,
        DateTimeOffset Now)
    {
        public string SessionLabel => SessionWindow.Label(SessionState);
        public string Et(DateTimeOffset utc) => utc.ToOffset(Clock.ExchangeOffset).ToString("HH:mm");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TradingEnabled.Set(policies.Risk.TradingEnabled ? 1 : 0);

        logger.LogInformation(
            "Day-trading agent started. mode={Mode} tradingEnabled={TradingEnabled} symbols={SymbolCount} interval={IntervalSeconds}s "
            + "dataFeed={DataFeed} entries={EntryStart}-{EntryCutoff} ET flatten={Flatten} ET capital={Capital} "
            + "perTrade={PerTrade} maxPositions={MaxPositions} maxExposure={MaxExposure} dailyLoss={DailyLoss} "
            + "stop={StopPercent}% target={TargetPercent}%",
            options.TradingMode, policies.Risk.TradingEnabled, policies.Allowlist.Count, options.EvaluationIntervalSeconds,
            options.NormalisedDataFeed, policies.Session.EntryStartEt, policies.Session.EntryCutoffEt, policies.Session.FlattenEt,
            policies.Risk.StrategyCapital, policies.Risk.MaxNotionalPerTrade, policies.Risk.MaxConcurrentPositions,
            policies.Risk.MaxTotalExposure, policies.Risk.MaxDailyLoss, policies.Exits.StopLossPercent, policies.Exits.TakeProfitPercent);

        if (options.NormalisedDataFeed == AgentOptions.DefaultFeed)
            logger.LogWarning(
                "Quoting from the free 'iex' feed, which carries a small share of US equity volume. Expect entries to be "
                + "suppressed on symbols that trade thinly on it. Set ALPACA_DATA_FEED=sip (paid) before judging a strategy.");

        if (!policies.Risk.TradingEnabled)
            logger.LogWarning(
                "Order submission is DISABLED (observation mode). Decisions are evaluated and logged; no order is placed. "
                + "This also disables the end-of-day flatten.");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.EvaluationIntervalSeconds));
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
                // A failed cycle must never kill the loop.
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
        var executor = scope.ServiceProvider.GetRequiredService<IOrderExecutor>();
        var store = scope.ServiceProvider.GetRequiredService<IDecisionStore>();

        // ── 1. Session state ──────────────────────────────────────────────
        var clock = await market.GetMarketClockAsync(cancellationToken);
        MarketOpen.Set(clock.IsOpen ? 1 : 0);

        if (!clock.IsOpen)
        {
            SetSessionState(SessionState.MarketClosed);
            EntryWindowOpen.Set(0);
            SessionMinutesRemaining.Set(0);
            await ReportPositionsHeldWhileClosedAsync(accounts, clock, cancellationToken);
            state.RecordCycleSuccess(0, "market closed");
            CycleDuration.Observe(stopwatch.Elapsed.TotalSeconds);
            return;
        }

        var session = await market.GetSessionAsync(clock, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var schedule = SessionWindow.Schedule(session, policies.Session);
        var sessionState = SessionWindow.Resolve(clock.IsOpen, schedule, now);
        SetSessionState(sessionState);
        SessionMinutesRemaining.Set(Math.Max(0, (schedule.CloseUtc - now).TotalMinutes));

        var account = await accounts.GetAsync(cancellationToken);
        var ordersToday = await accounts.GetTodaysOrdersAsync(cancellationToken);
        OpenPositions.Set(account.OpenPositionCount);
        if (account.DayTradeCount is { } dayTrades) DayTradesUsed.Set(dayTrades);

        // Daily loss: once reached, entries stay blocked for the session even
        // if P&L recovers. Recorded once, when it first trips.
        if (account.DayPnl <= -policies.Risk.MaxDailyLoss && state.LatchDailyLockout(session.Date))
        {
            logger.LogWarning(
                "DAILY LOSS LOCKOUT: day P&L {DayPnl:0.00} reached the {Limit:0.00} limit. New entries are blocked for the rest of the {Date} session; exits continue.",
                account.DayPnl, policies.Risk.MaxDailyLoss, session.Date);
            await PersistAsync(store, SystemRecord("DAILY_LOCKOUT",
                $"Day P&L {account.DayPnl:0.00} reached the {policies.Risk.MaxDailyLoss:0.00} daily loss limit.", sessionState), cancellationToken);
        }
        var lockout = state.IsLockedOut(session.Date);
        DailyLockout.Set(lockout ? 1 : 0);

        // ── 2. Reconcile ──────────────────────────────────────────────────
        await ReconcileAsync(executor, store, ordersToday, session.Date, sessionState, cancellationToken);
        var uncertain = state.UncertainOrders().Count > 0;
        UncertainOrders.Set(state.UncertainOrders().Count);

        var cycle = new Cycle(clock, schedule, sessionState, account, ordersToday,
            ordersToday.Where(o => OpenOrderStatuses.Contains(o.Status)).Select(o => o.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase),
            lockout, uncertain, now);

        // ── 3. Exits ──────────────────────────────────────────────────────
        var closing = await RunExitPassAsync(cycle, coordinator, store, cancellationToken);

        // ── 4. Entries ────────────────────────────────────────────────────
        EntryWindowOpen.Set(sessionState == SessionState.EntryWindow ? 1 : 0);

        var blockedBecause =
            sessionState != SessionState.EntryWindow
                ? $"session is {cycle.SessionLabel} (entries {cycle.Et(schedule.EntryStartUtc)}–{cycle.Et(schedule.EntryCutoffUtc)} ET, flatten {cycle.Et(schedule.FlattenUtc)} ET)"
            : lockout ? "the daily loss lockout is active"
            : uncertain ? "an order's broker status is unconfirmed and is being reconciled"
            : null;

        if (blockedBecause is not null)
        {
            if (uncertain) logger.LogWarning("No new entries: {Reason}.", blockedBecause);
            else logger.LogInformation("No new entries: {Reason}.", blockedBecause);
            state.RecordCycleSuccess(0, $"exits only — {cycle.SessionLabel.ToLowerInvariant()}");
            CycleDuration.Observe(stopwatch.Elapsed.TotalSeconds);
            return;
        }

        var evaluated = 0;
        foreach (var symbol in policies.Allowlist.OrderBy(s => s, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = closing.Contains(symbol)
                ? "closing"
                : await EvaluateSymbolAsync(symbol, cycle, market, strategy, coordinator, store, cancellationToken);
            Evaluations.WithLabels(symbol, outcome).Inc();
            evaluated++;
        }

        state.RecordCycleSuccess(evaluated, "evaluated");
        CycleDuration.Observe(stopwatch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// rules/execution-rules.md and audit-rules.md: settle any order whose
    /// status was unclear by asking the broker by client_order_id, and write
    /// each order's final outcome (fill, cancel, reject, expiry) to the audit.
    /// </summary>
    private async Task ReconcileAsync(
        IOrderExecutor executor,
        IDecisionStore store,
        IReadOnlyList<AccountSnapshotProvider.OrderSnapshot> ordersToday,
        DateOnly sessionDate,
        SessionState sessionState,
        CancellationToken cancellationToken)
    {
        foreach (var (clientOrderId, symbol) in state.UncertainOrders())
        {
            BrokerOrderResult? found;
            try
            {
                found = await executor.GetOrderByClientOrderIdAsync(clientOrderId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Order {ClientOrderId} ({Symbol}) is still unconfirmed; entries stay blocked.", clientOrderId, symbol);
                continue;
            }

            state.ResolveUncertain(clientOrderId);
            var reason = found is null
                ? "Reconciled: the broker confirms no such order exists; nothing was placed."
                : $"Reconciled: the broker holds the order with status {found.Status}.";
            logger.LogWarning("RECONCILED {ClientOrderId} ({Symbol}): {Reason}", clientOrderId, symbol, reason);

            await PersistAsync(store, new DecisionRecord
            {
                DecidedAtUtc = DateTimeOffset.UtcNow,
                Symbol = symbol,
                Approved = true,
                DecisionCode = "RECONCILED",
                DecisionReason = reason,
                ClientOrderId = clientOrderId,
                BrokerOrderId = found?.BrokerOrderId,
                BrokerStatus = found?.Status ?? "not_found",
                FilledQuantity = found?.FilledQuantity,
                FilledAveragePrice = found?.FilledAveragePrice,
                TradingEnabled = policies.Risk.TradingEnabled,
                MarketOpen = true,
                Pod = PodName,
                SessionState = SessionWindow.Label(sessionState),
            }, cancellationToken);
        }

        foreach (var order in ordersToday.Where(o => o.IsTerminal && o.BrokerOrderId.Length > 0))
        {
            if (state.IsOutcomeAudited(sessionDate, order.BrokerOrderId, order.Status)) continue;
            try
            {
                await store.RecordOrderOutcomeAsync(new OrderOutcome(
                    order.BrokerOrderId, order.Status, order.FilledQuantity, order.FilledAveragePrice,
                    order.FilledAtUtc, order.CanceledAtUtc ?? order.FailedAtUtc ?? order.ExpiredAtUtc), cancellationToken);
                // Marked only after the write succeeds, so a failed write is retried next cycle.
                state.MarkOutcomeAudited(sessionDate, order.BrokerOrderId, order.Status);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AuditFailures.Inc();
                logger.LogError(ex, "Could not record the outcome of order {BrokerOrderId}.", order.BrokerOrderId);
            }
        }
    }

    private async Task<IReadOnlySet<string>> RunExitPassAsync(
        Cycle cycle, TradingCoordinator coordinator, IDecisionStore store, CancellationToken cancellationToken)
    {
        var closing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cycle.Account.Positions.Count == 0) return closing;

        var openedAt = DerivePositionOpenTimes(cycle.OrdersToday);

        foreach (var raw in cycle.Account.Positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = openedAt.TryGetValue(raw.Symbol, out var opened) ? raw with { OpenedAtUtc = opened } : raw;

            if (!position.PnlKnown)
                logger.LogWarning(
                    "{Symbol}: the broker did not report unrealised P&L. The stop and target are suspended for this position; the flatten still applies.",
                    position.Symbol);

            var exit = ExitManager.Evaluate(position, policies.Exits, cycle.Schedule, cycle.Now);
            if (!exit.ShouldExit) continue;

            var proposal = new StrategySignal(
                position.Symbol, TradeAction.Sell, position.MarketValue, 1.0m, "exit-manager", exit.Explanation, cycle.Now,
                EntryType: "close_position",
                Invalidation: null,
                Target: $"{ExitReasonLabel(exit.Reason)}: close the whole position now");

            var accountState = RiskState(cycle, position.Symbol, position.MarketValue);

            TradingRunResult result;
            try
            {
                result = await coordinator.ProcessAsync(
                    proposal, accountState, policies.Risk, policies.Allowlist, cycle.Now, OrderIntent.Exit, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "COULD NOT CLOSE {Symbol} ({Reason}). The position is still open.", position.Symbol, exit.Reason);
                continue;
            }

            await PersistAsync(store, DecisionRecord.From(
                proposal, result.Decision, result.BrokerOrder, policies.Risk.TradingEnabled, true, PodName, cycle.SessionLabel), cancellationToken);

            if (result.Submitted)
            {
                closing.Add(position.Symbol);
                Exits.WithLabels(position.Symbol, ExitReasonLabel(exit.Reason)).Inc();
                logger.LogWarning(
                    "CLOSING {Symbol} {Reason}: {Explanation} unrealisedPnl={Pnl} ({PnlPct}%) brokerStatus={BrokerStatus}",
                    position.Symbol, exit.Reason, exit.Explanation,
                    Format(position.UnrealizedPnl), Format(position.UnrealizedPnlFraction * 100m), result.BrokerOrder?.Status);
            }
            else
            {
                logger.LogWarning("EXIT REJECTED {Symbol} ({Reason}): {Code} — {Message}",
                    position.Symbol, exit.Reason, result.Code, result.Message);
            }
        }

        return closing;
    }

    private async Task<string> EvaluateSymbolAsync(
        string symbol,
        Cycle cycle,
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
            var reason = ex is MarketDataException typed ? RejectionLabel(typed.Reason) : RejectionLabel(MarketDataRejection.Unavailable);
            DataRejections.WithLabels(symbol, reason).Inc();
            logger.LogInformation("HOLD {Symbol}: market data unusable ({Reason}) — {Detail}", symbol, reason, ex.Message);
            await PersistAsync(store, DecisionRecord.NoData(
                symbol, ex.Message, policies.Risk.TradingEnabled, true, PodName, cycle.SessionLabel), cancellationToken);
            return "no_data";
        }

        var proposal = WithInvalidationAndTarget(strategy.Evaluate(inputs, policies.Strategy, now), inputs.LastPrice, cycle);
        var accountState = RiskState(cycle, symbol, cycle.Account.PositionNotionalBySymbol.GetValueOrDefault(symbol, 0m));

        TradingRunResult result;
        try
        {
            result = await coordinator.ProcessAsync(
                proposal, accountState, policies.Risk, policies.Allowlist, now, OrderIntent.Entry, cancellationToken);
        }
        catch (OrderStatusUncertainException ex)
        {
            // Never blindly retry. Block new entries until the broker answers.
            state.MarkUncertain(ex.ClientOrderId, ex.Symbol);
            UncertainOrders.Set(state.UncertainOrders().Count);
            logger.LogError(ex, "ORDER STATUS UNCERTAIN {ClientOrderId} ({Symbol}). New entries are blocked until it is reconciled.", ex.ClientOrderId, ex.Symbol);
            await PersistAsync(store, new DecisionRecord
            {
                DecidedAtUtc = now,
                Symbol = symbol,
                StrategyName = proposal.StrategyName,
                Action = proposal.Action,
                ProposedNotional = proposal.ProposedNotional,
                Approved = true,
                DecisionCode = "ORDER_STATE_UNCERTAIN",
                DecisionReason = ex.Message,
                ClientOrderId = ex.ClientOrderId,
                TradingEnabled = policies.Risk.TradingEnabled,
                MarketOpen = true,
                Pod = PodName,
                SessionState = cycle.SessionLabel,
            }, cancellationToken);
            return "order_uncertain";
        }

        await PersistAsync(store, DecisionRecord.From(
            proposal, result.Decision, result.BrokerOrder, policies.Risk.TradingEnabled, true, PodName, cycle.SessionLabel), cancellationToken);

        if (result.Submitted)
        {
            logger.LogWarning(
                "ORDER SUBMITTED {Symbol} {Action} notional={Notional} clientOrderId={ClientOrderId} brokerStatus={BrokerStatus} invalidation=\"{Invalidation}\" target=\"{Target}\"",
                symbol, proposal.Action, proposal.ProposedNotional, result.BrokerOrder?.ClientOrderId, result.BrokerOrder?.Status,
                proposal.Invalidation, proposal.Target);
            return "submitted";
        }

        logger.LogInformation("{Decision} {Symbol}: {Code} — {Message} (action={Action} confidence={Confidence:0.00})",
            result.Status, symbol, result.Code, result.Message, proposal.Action, proposal.Confidence);
        return result.Code.ToLowerInvariant();
    }

    /// <summary>
    /// v3 trade-proposal skill: every BUY names what invalidates it and what
    /// ends it. Derived from the deterministic exit policy, so the recorded
    /// invalidation is the one the exit manager will actually enforce.
    /// </summary>
    private StrategySignal WithInvalidationAndTarget(StrategySignal proposal, decimal lastPrice, Cycle cycle)
    {
        var flatten = cycle.Et(cycle.Schedule.FlattenUtc);
        return proposal.Action switch
        {
            TradeAction.Buy => proposal with
            {
                EntryType = "market",
                Invalidation = $"Stop at {lastPrice * (1 - policies.Exits.StopLossPercent / 100m):0.00} ({-policies.Exits.StopLossPercent:0.00}% from {lastPrice:0.00})",
                Target = $"Take profit at {lastPrice * (1 + policies.Exits.TakeProfitPercent / 100m):0.00} (+{policies.Exits.TakeProfitPercent:0.00}%), "
                         + $"after {policies.Exits.MaxHoldTime.TotalMinutes:0} minutes, or flatten by {flatten} ET",
            },
            TradeAction.Sell => proposal with
            {
                EntryType = "market",
                Invalidation = "Momentum no longer bearish",
                Target = "Reduce or close the existing long position",
            },
            _ => proposal,
        };
    }

    private AccountRiskState RiskState(Cycle cycle, string symbol, decimal existingPositionNotional) => new(
        Cash: cycle.Account.Cash,
        PortfolioExposure: cycle.Account.PortfolioExposure,
        DailyRealizedPnl: cycle.Account.DayPnl,
        OpenPositionCount: cycle.Account.OpenPositionCount,
        TotalOrdersToday: cycle.OrdersToday.Count,
        OrdersForSymbolToday: cycle.OrdersToday.Count(o => string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase)),
        MarketOpen: cycle.Clock.IsOpen,
        // Startup refuses any endpoint other than the paper host over HTTPS.
        IsPaperEndpoint: true,
        HasOpenOrderForSymbol: cycle.OpenOrderSymbols.Contains(symbol),
        ExistingPositionNotional: existingPositionNotional,
        Equity: cycle.Account.Equity,
        DayTradeCount: cycle.Account.DayTradeCount,
        SessionState: cycle.SessionState,
        DailyLossLockout: cycle.Lockout,
        OrderStateUncertain: cycle.Uncertain);

    private DecisionRecord SystemRecord(string code, string reason, SessionState sessionState) => new()
    {
        DecidedAtUtc = DateTimeOffset.UtcNow,
        Symbol = "*",
        Approved = false,
        DecisionCode = code,
        DecisionReason = reason,
        TradingEnabled = policies.Risk.TradingEnabled,
        MarketOpen = true,
        Pod = PodName,
        SessionState = SessionWindow.Label(sessionState),
    };

    private void SetSessionState(SessionState current)
    {
        foreach (var s in Enum.GetValues<SessionState>())
            SessionStateGauge.WithLabels(SessionWindow.Label(s)).Set(s == current ? 1 : 0);
        state.RecordSessionState(SessionWindow.Label(current));
    }

    /// <summary>When each open position was opened: the first buy fill after the last sell fill.</summary>
    private static Dictionary<string, DateTimeOffset> DerivePositionOpenTimes(IReadOnlyList<AccountSnapshotProvider.OrderSnapshot> ordersToday)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in ordersToday.Where(o => o.FilledAtUtc is not null).GroupBy(o => o.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            var lastSell = group.Where(o => o.Side == "sell").Select(o => o.FilledAtUtc!.Value).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
            var firstBuyAfter = group.Where(o => o.Side == "buy" && o.FilledAtUtc!.Value > lastSell)
                .Select(o => o.FilledAtUtc!.Value).DefaultIfEmpty(DateTimeOffset.MinValue).Min();
            if (firstBuyAfter > DateTimeOffset.MinValue) result[group.Key] = firstBuyAfter;
        }
        return result;
    }

    private async Task ReportPositionsHeldWhileClosedAsync(AccountSnapshotProvider accounts, MarketClock clock, CancellationToken cancellationToken)
    {
        try
        {
            var account = await accounts.GetAsync(cancellationToken);
            OpenPositions.Set(account.OpenPositionCount);
            if (account.DayTradeCount is { } dayTrades) DayTradesUsed.Set(dayTrades);

            if (account.OpenPositionCount == 0)
            {
                logger.LogInformation("Market is closed and the account is flat. nextOpen={NextOpenUtc:o}", clock.NextOpenUtc);
                return;
            }

            logger.LogWarning(
                "Market is closed and {Count} position(s) are still open: {Symbols}. A day-trading account should be flat overnight — check why the flatten did not complete.",
                account.OpenPositionCount, string.Join(", ", account.Positions.Select(p => p.Symbol).OrderBy(s => s, StringComparer.Ordinal)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Market is closed; could not read the account to confirm the book is flat.");
        }
    }

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
        var volumes = bars.Select(b => (decimal)b.Volume).ToArray();
        var averageVolume = volumes.Average();

        return new MomentumInputs(
            symbol.ToUpperInvariant(),
            quote.Mid,
            closes.TakeLast(fastWindow).Average(),
            closes.Average(),
            averageVolume <= 0 ? 0m : volumes.TakeLast(fastWindow).Average() / averageVolume,
            quote.SpreadBps,
            quote.TimestampUtc);
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

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }
}
