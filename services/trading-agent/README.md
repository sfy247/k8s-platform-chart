# Claude Trading Agent — Paper Trading Reference Implementation

A safety-first, paper-trading system for evaluating an AI-assisted stock-trading workflow before any real capital is exposed.

## Core design principles

1. **Paper trading only by default.** The repository ships with `TRADING_MODE=PAPER` and no live endpoint in configuration.
2. **Deterministic risk controls.** Claude may research and propose trades, but only C# risk code can approve an order.
3. **Separation of duties.** Research, strategy, risk, execution, and portfolio concerns are isolated behind explicit contracts.
4. **No unrestricted agent execution.** The AI never receives broker secrets and never calls the broker directly.
5. **Idempotent orders.** Every approved order gets a unique `client_order_id` so retries do not intentionally create duplicate orders.
6. **Fail closed.** Missing data, malformed agent output, stale market data, or risk-check failures block execution.
7. **Observable decisions.** Every proposal, risk decision, and execution result is structured and auditable.
8. **Small-account realism.** The default risk profile models a $100 paper account and uses fractional-notional orders.
9. **No leverage or shorting.** Margin, shorting, options, crypto, and extended-hours trading are disabled.
10. **Manual promotion only.** Moving from paper to live trading must require deliberate code/configuration changes and new credentials.

## Architecture

```text
Market Data
    │
    ▼
Research / Strategy
    │  TradeProposal
    ▼
Deterministic Risk Engine
    │  ApprovedOrder
    ▼
Execution Service
    │
    ▼
Alpaca PAPER API
    │
    ├── Orders / fills
    └── Account state

Portfolio + Audit consume the same event stream.
```

## Default paper risk policy

- Starting capital assumption: **$100**
- Maximum new position notional: **$10**
- Maximum concurrent positions: **3**
- Maximum daily realized loss: **$3**
- Minimum cash reserve: **$10**
- Margin: **disabled**
- Shorting: **disabled**
- Options: **disabled**
- Crypto: **disabled**
- Extended hours: **disabled**
- Fractional-notional market orders: **allowed only during regular market hours and after spread/data-freshness checks**

These are testing controls, not a promise of profitability.

## Repository layout

```text
.
├── CLAUDE.md
├── README.md
├── .env.example
├── .gitignore
├── Directory.Build.props
├── ClaudeTradingAgent.sln
├── agents/
├── config/
├── src/
│   ├── TradingAgent/
│   ├── MarketData/
│   ├── Strategy/
│   ├── RiskManagement/
│   ├── Execution/
│   └── Portfolio/
├── tests/
├── docker/
└── logs/
```

## Operating modes

### Observation mode

The system produces signals and risk decisions but execution is blocked.

```text
TRADING_ENABLED=false
```

### Paper execution mode

Orders can be submitted only to the configured paper endpoint after all checks pass.

```text
TRADING_MODE=PAPER
TRADING_ENABLED=true
ALPACA_TRADING_BASE_URL=https://paper-api.alpaca.markets
```

## Recommended validation sequence

1. Confirm account and clock retrieval.
2. Confirm symbol eligibility and fresh quote retrieval.
3. Run strategy in observation mode.
4. Verify risk rejections intentionally fire.
5. Verify duplicate order IDs are handled idempotently.
6. Verify kill switch blocks execution.
7. Enable paper execution.
8. Reconcile broker orders/fills against local records after every run.
9. Review performance over a meaningful sample instead of changing rules after individual wins/losses.

## Important paper-trading limitations

Paper trading is useful for software and strategy testing, but simulated fills can differ from live fills because liquidity, fill assumptions, and market impact are not identical to real trading. Treat positive paper results as evidence to investigate further, not proof that a live strategy will perform the same way.

## Backtesting

Replays historical bars through the **production** strategy and risk engine —
not a reimplementation — and reports whether the result beat doing nothing.

```bash
export ALPACA_API_KEY_ID=... ALPACA_API_SECRET_KEY=...
dotnet run --project src/Backtest -- --days 90 --timeframe 5Min --cash 100
```

| Flag | Default | Notes |
|---|---|---|
| `--symbols` | the five allowlisted | comma separated |
| `--days` | 30 | history to replay |
| `--timeframe` | 5Min | `1Min`, `5Min`, `15Min`, `1Hour`, `1Day` |
| `--cash` | 100 | starting capital |
| `--spread-bps` | 5 | assumed round-trip cost — the result is most sensitive to this |
| `--config` | `config/trading.json` | policies come from the deployed config |

### Two properties that decide whether a backtest is honest

**No look-ahead.** A signal from the bar closing at T is filled at the *open
of the bar after it*. Filling at T's close means trading on a price you could
not have known, and is the most common way a backtest lies.

**Costs are paid.** Every entry and exit crosses the spread. A strategy
paying 0.5% round trip must be right by more than 0.5% before it has made
anything.

### Strategies available

Four premises, deliberately not variations on a theme:

| Strategy | Premise |
|---|---|
| `momentum-crossover` | a short average crossing a long one marks a trend that continues |
| `rsi-mean-reversion` | short-term moves overshoot and revert |
| `vwap-reversion` | institutional orders are benchmarked to VWAP, so flow pulls price back |
| `opening-range-breakout` | overnight information resolves in the opening range |

Momentum and mean reversion are opposite bets. If both look profitable on the
same data, that is evidence of overfitting rather than two independent edges.

```bash
dotnet run --project src/Backtest -- --days 365 --timeframe 1Day --unconstrained
```

`--unconstrained` relaxes the risk caps. The deployed limits are a safety
policy, not an evaluation tool: with $10 positions and 8 orders a day against
$100, a strategy gets three or four trades in a quarter, which says nothing
about whether the signal is any good. Use it to measure the signal; never to
decide what to deploy.

### Results as of 2026-08-29

The shipped momentum strategy, measured against buying the same five symbols
and doing nothing:

| Window | Strategy | Buy and hold | Difference |
|---|---|---|---|
| 30d, 5Min | +3.97% | +8.95% | **−4.98%** |
| 90d, 5Min | −0.35% | +1.19% | **−1.54%** |
| 90d, 5Min, 25bps spread | +0.50% | +1.19% | **−0.69%** |
| 180d, 15Min | +1.78% | +24.84% | **−23.07%** |

It underperformed in every configuration tested. The 180-day window is the
clearest: the market rose 24.84% and the strategy captured 1.78%, because it
holds cash through most of the move — 8,336 of 8,845 evaluations returned
NO_TRADE.

Part of that gap is structural: `maxPortfolioExposure` of $30 against $100
of cash caps the strategy at 30% invested while the benchmark is fully
invested. That explains some of the shortfall, not all of it — 30% of a
24.84% rally would still have been around 7.5%.

Sample sizes are small (3–5 round trips per window), so the magnitudes are
noisy. The direction is not: no configuration beat holding.

### All four strategies, measured

With the risk caps relaxed so each gets enough trades to mean something.
90 days of 5-minute bars:

| Strategy | Return | vs hold | Trades | Win rate |
|---|---|---|---|---|
| rsi-mean-reversion | −0.77% | −1.96% | 17 | 65% |
| opening-range-breakout | −1.96% | −3.15% | 45 | 29% |
| vwap-reversion | −3.88% | −5.07% | 39 | 21% |
| momentum-crossover | −11.42% | −12.61% | 52 | 25% |
| *buy and hold* | *+1.19%* | — | *0* | — |

Every one loses money in absolute terms once it trades enough to be
measured. The deployed risk limits were not protecting a good strategy;
they were hiding a bad one by preventing it from trading.

### The lesson worth keeping: one window proves nothing

On daily bars, RSI mean reversion looked like an edge — until the window
moved:

| Window | RSI | Buy and hold | |
|---|---|---|---|
| 1 year | +39.66% | +28.44% | beat by 11% |
| 2 years | +50.12% | +62.45% | lost by 12% |
| 3 years | +41.43% | +147.29% | **lost by 106%** |

Same strategy, same symbols, same costs. The one-year result was the window,
not the signal — and the window was chosen arbitrarily, which is exactly how
this trap is usually sprung.

Note also that RSI wins **96% of its trades** across all three windows and
still loses badly. It takes small gains and sits in cash, missing the large
moves that produce most of the return. **Win rate is not profitability**, and
a strategy advertised on win rate is usually hiding this.

`vwap-reversion` and `opening-range-breakout` correctly produce zero trades
on daily bars: both are intraday concepts and have nothing to say about a
daily series. A strategy that silently traded anyway would be the bug.

> None of this says short-term trading cannot work. It says these four
> well-known technical patterns, on the most efficiently priced stocks in the
> world, do not survive costs. That is the expected result, and it took
> minutes to establish rather than months of paper trading or a funded
> account.
