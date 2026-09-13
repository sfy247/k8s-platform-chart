using ClaudeTradingAgent.RiskManagement;
using ClaudeTradingAgent.Strategy;
using Xunit;

namespace ClaudeTradingAgent.Tests;

/// <summary>The fifteen required checks of rules/risk-management-rules.md, entries and exits.</summary>
public sealed class RiskEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 18, 30, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<string> Symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AAPL" };

    private static StrategySignal Buy(decimal notional = 10m) => new("AAPL", TradeAction.Buy, notional, 0.8m, "momentum-v1", "test", Now);
    private static StrategySignal SellAll(decimal notional = 10m) => new("AAPL", TradeAction.Sell, notional, 1.0m, "exit-manager", "flatten", Now);

    private static RiskPolicy Policy(bool tradingEnabled = true) => new()
    {
        TradingEnabled = tradingEnabled,
        RequirePaperMode = true,
        StrategyCapital = 100m,
        MaxNotionalPerTrade = 10m,
        MaxConcurrentPositions = 2,
        MaxTotalExposure = 20m,
        MaxDailyLoss = 3m,
        MaxEstimatedLossPerTrade = 1m,
        StopLossPercent = 0.75m,
        MaxDataAge = TimeSpan.FromSeconds(10),
        MaxOrdersPerSymbolPerDay = 6,
        MaxTotalOrdersPerDay = 30,
        PdtEquityThreshold = 25_000m,
        MaxDayTradesUnderPdt = 3,
    };

    private static AccountRiskState State(
        bool isPaper = true, decimal dailyPnl = 0m, bool hasOpenOrder = false, decimal equity = 100_000m, int? dayTrades = 0,
        decimal position = 0m, int positions = 0, decimal exposure = 0m, decimal cash = 100_000m, int totalOrders = 0,
        int symbolOrders = 0, bool marketOpen = true, SessionState session = SessionState.EntryWindow,
        bool lockout = false, bool uncertain = false) =>
        new(cash, exposure, dailyPnl, positions, totalOrders, symbolOrders, marketOpen, isPaper, hasOpenOrder, position,
            equity, dayTrades, session, lockout, uncertain);

    private static RiskDecision Eval(StrategySignal p, AccountRiskState s, RiskPolicy? policy = null, OrderIntent intent = OrderIntent.Entry) =>
        new RiskEngine().Evaluate(p, s, policy ?? Policy(), Symbols, Now, intent);

    private static void Rejected(string code, RiskDecision d) { Assert.False(d.Approved); Assert.Equal(code, d.Code); }

    // ── Entries ──────────────────────────────────────────────────────────

    [Fact]
    public void Approves_a_valid_entry_in_the_entry_window()
    {
        var d = Eval(Buy(), State());
        Assert.True(d.Approved);
        Assert.Equal(10m, d.Order!.Notional);
        Assert.Equal(OrderIntent.Entry, d.Order.Intent);
    }

    [Fact] public void Rejects_a_non_paper_endpoint() => Rejected("NOT_PAPER", Eval(Buy(), State(isPaper: false)));
    [Fact] public void Rejects_when_the_kill_switch_is_off() => Rejected("KILL_SWITCH", Eval(Buy(), State(), Policy(tradingEnabled: false)));
    [Fact] public void Rejects_when_the_market_is_closed() => Rejected("MARKET_CLOSED", Eval(Buy(), State(marketOpen: false)));

    [Theory]
    [InlineData(SessionState.PreMarketDisabled)]
    [InlineData(SessionState.ManagementOnly)]
    [InlineData(SessionState.FlattenWindow)]
    [InlineData(SessionState.MarketClosed)]
    public void Only_the_entry_window_permits_new_entries(SessionState session) =>
        Rejected("OUTSIDE_ENTRY_WINDOW", Eval(Buy(), State(session: session)));

    [Fact]
    public void A_caller_that_does_not_state_the_session_gets_no_entries()
    {
        // Fail closed: the default session state is MARKET_CLOSED, not ENTRY_WINDOW.
        var unstated = new AccountRiskState(100_000m, 0m, 0m, 0, 0, 0, true, true, false, 0m, Equity: 100_000m, DayTradeCount: 0);
        Rejected("OUTSIDE_ENTRY_WINDOW", Eval(Buy(), unstated));
    }

    [Fact] public void Rejects_stale_data() => Rejected("STALE_DATA", Eval(Buy() with { DataTimestampUtc = Now.AddSeconds(-30) }, State()));
    [Fact] public void Rejects_more_than_the_per_trade_notional() => Rejected("POSITION_LIMIT", Eval(Buy(12m), State()));
    [Fact] public void Rejects_a_third_concurrent_position() => Rejected("POSITION_COUNT_LIMIT", Eval(Buy(), State(positions: 2, exposure: 10m)));
    [Fact] public void Rejects_exposure_beyond_the_total_limit() => Rejected("EXPOSURE_LIMIT", Eval(Buy(), State(positions: 1, exposure: 15m)));
    [Fact] public void Refuses_to_add_to_an_existing_position() => Rejected("PYRAMIDING_BLOCKED", Eval(Buy(), State(position: 10m, positions: 1, exposure: 10m)));

    [Fact]
    public void Sizes_against_strategy_capital_not_the_broker_balance()
    {
        // The broker shows $100,000. The strategy manages $15, with $8 deployed.
        var policy = Policy() with { StrategyCapital = 15m };
        Rejected("STRATEGY_CAPITAL", Eval(Buy(), State(positions: 1, exposure: 8m, cash: 100_000m), policy));
    }

    [Fact]
    public void Losses_today_reduce_the_capital_available()
    {
        var policy = Policy() with { StrategyCapital = 11m };
        Assert.True(Eval(Buy(), State(), policy).Approved);
        Rejected("STRATEGY_CAPITAL", Eval(Buy(), State(dailyPnl: -2m), policy));
    }

    [Fact] public void Rejects_an_order_broker_cash_cannot_cover() => Rejected("INSUFFICIENT_CASH", Eval(Buy(), State(cash: 5m)));

    [Fact]
    public void Rejects_an_entry_whose_loss_at_the_stop_exceeds_the_per_trade_limit()
    {
        // $10 at a 0.75% stop risks $0.075.
        Rejected("PER_TRADE_LOSS_LIMIT", Eval(Buy(), State(), Policy() with { MaxEstimatedLossPerTrade = 0.05m }));
        Assert.True(Eval(Buy(), State(), Policy() with { MaxEstimatedLossPerTrade = 0.08m }).Approved);
    }

    [Fact] public void Rejects_at_the_daily_loss_limit() => Rejected("DAILY_LOSS_LIMIT", Eval(Buy(), State(dailyPnl: -3m)));

    [Fact]
    public void The_daily_lockout_holds_for_the_session_even_after_pnl_recovers() =>
        Rejected("DAILY_LOSS_LIMIT", Eval(Buy(), State(dailyPnl: 1m, lockout: true)));

    [Fact]
    public void An_unreconciled_order_blocks_every_new_entry() =>
        Rejected("ORDER_STATE_UNCERTAIN", Eval(Buy(), State(uncertain: true)));

    [Fact] public void Rejects_duplicate_symbol_exposure() => Rejected("DUPLICATE_EXPOSURE", Eval(Buy(), State(hasOpenOrder: true)));
    [Fact] public void Rejects_a_sell_that_would_open_a_short() => Rejected("NO_LONG_POSITION", Eval(SellAll(), State()));
    [Fact] public void Rejects_a_sell_larger_than_the_long() => Rejected("SHORTING_BLOCKED", Eval(SellAll(15m), State(position: 10m)));
    [Fact] public void Rejects_past_the_daily_order_limit() => Rejected("ORDER_RATE_LIMIT", Eval(Buy(), State(totalOrders: 30)));

    [Fact] public void Rejects_at_the_pdt_limit_under_the_equity_threshold() => Rejected("PDT_LIMIT", Eval(Buy(), State(equity: 20_000m, dayTrades: 3)));
    [Fact] public void Refuses_below_the_pdt_threshold_without_a_count() => Rejected("PDT_COUNT_UNKNOWN", Eval(Buy(), State(equity: 20_000m, dayTrades: null)));
    [Fact] public void Ignores_the_count_above_the_pdt_threshold() => Assert.True(Eval(Buy(), State(dayTrades: null)).Approved);
    [Fact] public void Rejects_when_equity_is_unknown() => Rejected("EQUITY_UNKNOWN", Eval(Buy(), State(equity: 0m)));

    // ── Exits: none of the entry-only limits may trap a position ─────────

    [Theory]
    [InlineData(SessionState.FlattenWindow)]
    [InlineData(SessionState.ManagementOnly)]
    [InlineData(SessionState.PreMarketDisabled)]
    public void Exits_are_allowed_outside_the_entry_window(SessionState session) =>
        Assert.True(Eval(SellAll(), State(position: 10m, session: session), intent: OrderIntent.Exit).Approved);

    [Fact]
    public void Exits_ignore_every_entry_only_limit()
    {
        var state = State(position: 45m, dailyPnl: -50m, lockout: true, uncertain: true, totalOrders: 999,
            equity: 20_000m, dayTrades: 3, session: SessionState.FlattenWindow);
        var d = Eval(SellAll(45m) with { DataTimestampUtc = Now.AddHours(-3) }, state, intent: OrderIntent.Exit);
        Assert.True(d.Approved);
        Assert.Equal(OrderIntent.Exit, d.Order!.Intent);
    }

    [Fact] public void Refuses_an_exit_while_a_close_is_working() => Rejected("DUPLICATE_EXPOSURE", Eval(SellAll(), State(position: 10m, hasOpenOrder: true), intent: OrderIntent.Exit));
    [Fact] public void Refuses_an_exit_when_the_kill_switch_is_off() => Rejected("KILL_SWITCH", Eval(SellAll(), State(position: 10m), Policy(tradingEnabled: false), OrderIntent.Exit));
    [Fact] public void Refuses_an_exit_when_the_market_is_closed() => Rejected("MARKET_CLOSED", Eval(SellAll(), State(position: 10m, marketOpen: false), intent: OrderIntent.Exit));
}
