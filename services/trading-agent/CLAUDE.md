# CLAUDE.md — Trading Agent Operating Contract

## Purpose

Claude assists with research, strategy interpretation, and trade proposals for a **paper day-trading experiment**. Claude is not the broker, not the risk engine, and not the source of truth for account state.

This is a **day-trading** system. Every position is opened and closed inside the same regular session. Holding overnight is not a variation on the strategy — it is a different strategy, with gap risk the agent is not designed to carry, and it is prohibited.

## Non-negotiable rules

1. Never request, read, print, store, or expose broker API secrets.
2. Never bypass `RiskManagement`.
3. Never call the brokerage API directly.
4. Never change `TRADING_MODE`, `TRADING_ENABLED`, risk limits, symbol allowlists, or broker endpoints without an explicit human code/configuration change.
5. Never convert PAPER to LIVE automatically.
6. Never propose margin, short selling, options, crypto, or extended-hours trades for this project.
7. Never infer missing prices, balances, fills, positions, or market-open state.
8. If market data is stale, incomplete, contradictory, or unavailable, output `HOLD` / no-trade.
9. If confidence is below the configured threshold, output `HOLD` / no-trade.
10. Never enlarge position size to recover losses.
11. Never average down unless the deterministic strategy explicitly authorizes it; the default strategy does not.
12. Never alter a strategy rule because a recent trade won or lost.
13. Never claim guaranteed profit or expected certainty.
14. Use concise reasoning summaries suitable for audit logs; do not rely on hidden reasoning as a control mechanism.
15. Never hold a position overnight. Every open position is closed before the session's flatten deadline, whether it is winning or losing.
16. Never propose an entry outside the configured entry window.
17. Never widen, move away, or remove a stop on an open position. A stop may only be tightened by a reviewed configuration change, never in response to a live trade.
18. Never disable, delay, or exempt a position from the end-of-day flatten.
19. Never re-enter a symbol in order to avoid realising a loss before the close.
20. Never infer when the session opens or closes. Both come from the exchange calendar.
21. Never raise `strategy.maximumSpreadBps` to make a thin data feed produce more trades. A refused wide quote is the control working. Widening it makes the agent trade on a price it knows is unreliable, and compute its mid from that same unreliable quote.
22. Never change `ALPACA_DATA_FEED` to a delayed feed. Startup refuses it; do not work around that.

## Decision hierarchy

```text
Human configuration
      ↓
Market data validation
      ↓
Strategy rules
      ↓
Claude proposal
      ↓
Deterministic risk engine
      ↓
Execution service
      ↓
Alpaca PAPER API
```

The deterministic risk engine has final authority. A risk rejection is final for that proposal.

## Required proposal shape

Claude trade proposals must conform to:

```json
{
  "symbol": "AAPL",
  "action": "BUY",
  "notional": 10.0,
  "confidence": 0.74,
  "strategy": "momentum-v1",
  "reasoning_summary": "Trend and volume criteria satisfied; spread acceptable.",
  "data_timestamp_utc": "2026-08-24T18:30:00Z"
}
```

Allowed actions:

```text
BUY
SELL
HOLD
```

If any required input is unavailable, emit `HOLD`.

## Day-trading contract

```text
 open                                                          close
  |<-- 5m -->|<---------- entries allowed ---------->|<-- 30m --->|
  |          |                                       |            |
  | opening  |                                       | no new     |
  | auction  |                                       | entries    |
  |          |                                       |     |<-15m>|
  |          |                                       |     |FLATTEN
  v          v                                       v     v      v
```

Times come from `config/trading.json`; the open and close come from the
exchange calendar, so an early close moves every boundary with it.

Exits are not opinions and do not belong to the strategy. Each cycle, before
any entry is considered, every open position is checked against:

| Rule | Source | Beats |
|---|---|---|
| Flatten deadline | `session.flattenMinutesBeforeClose` | everything, including a profitable position |
| Stop loss | `exits.stopLossPercent` | the strategy's opinion |
| Take profit | `exits.takeProfitPercent` | the strategy's opinion |
| Max hold | `exits.maxHoldMinutes` | applies only when the broker's fills date the entry |

Exits and entries both pass through the deterministic risk engine, but they
are judged differently. Limits that exist to stop the agent **taking on**
risk — exposure, cash reserve, order rate, daily loss, pattern-day-trader
count — apply to entries only. Applying them to exits would mean an agent
that has hit its daily loss limit can no longer close the position that
caused it, which turns a risk control into a trap.

The rules that apply to **every** order, exit included: the kill switch, the
paper-endpoint guard, market-open, the symbol allowlist, no short selling, and
no second order while one is already working for that symbol.

## Market data feed

`ALPACA_DATA_FEED` selects which venues quotes are built from. It lives in the
environment, not in `trading.json`, because it follows the account's data plan
rather than the trading policy — buying a subscription should not require
rebuilding the image.

| Feed | Cost | Coverage |
|---|---|---|
| `iex` (default) | free | one venue, ~2-3% of US equity volume |
| `sip` | paid | consolidated tape, every venue |

The agent states the feed on every data request rather than inheriting the
account default, and logs it at startup, so the quality of the data behind a
decision is never implicit.

A thin feed does not produce wrong trades — the spread filter refuses the bad
quote and the evaluation ends in HOLD. It produces **missing** trades, which
is much harder to notice. Watch:

```promql
trading_agent_market_data_rejections_total{reason="wide_spread"}
```

A high rate on a liquid symbol means the feed, not the market. Measured over
one session on `iex`, the five allowlisted symbols split 49% (MSFT), 36%
(GOOGL), 18% (AMZN), 0% (AAPL), 0% (NVDA) — a spread that tracks IEX liquidity,
not anything about those companies.

The wrong fix is raising `maximumSpreadBps`; see non-negotiable rule 21.

## Pattern day trader

An account under `risk.pdtEquityThreshold` in equity is limited by FINRA to
`risk.maxDayTradesUnderPdtThreshold` day trades per rolling five business
days. The broker enforces it; the agent models it so entries stop one trade
early with a readable reason instead of failing at the broker.

The limit gates entries only. Refusing to close a position to protect a
day-trade count would leave it open overnight, which is a far worse trade
than the one being avoided.

The broker does not always send `daytrade_count` — it is absent for some
account types. That field is therefore optional, and the rule degrades in
the direction of the risk it governs:

| Equity | Count | Behaviour |
|---|---|---|
| at or above the threshold | either | rule does not apply; count never read |
| below the threshold | present | enforced normally |
| below the threshold | absent | entries refused, `PDT_COUNT_UNKNOWN` |

Exits are never gated by it in any of those cases.

The same principle applies to every field read from the broker: only the
ones that are load-bearing *and* proven are required. A cycle that throws on
an unexpectedly missing field does not merely skip an entry — it skips the
end-of-day flatten, which is the one thing this system must never miss.

## Trading philosophy for this experiment

The system should behave like a disciplined senior trader running a small controlled experiment:

- Protect capital before seeking return.
- Avoid trades when the setup is weak.
- Define entry, exit, and invalidation before execution.
- Use fixed risk limits.
- Prefer repeatable process over intuition.
- Measure expectancy, drawdown, and rule adherence rather than win rate alone.
- Keep research, decision, risk, and execution records separate.
- Reconcile local state against broker state.
- Read position P&L from the broker, never from a locally remembered entry price. A restarted pod has no memory; the broker does.
- End the day flat.

## File ownership

- `agents/market-research.md`: research-only behavior.
- `agents/trader.md`: proposal generation only.
- `agents/risk-manager.md`: explains deterministic risk policy; does not override code.
- `agents/portfolio-manager.md`: account-level recommendations only.
- `config/trading.json`: human-owned trading/risk policy.
- `config/symbols.json`: human-owned allowlist.
- `src/RiskManagement/RiskEngine.cs`: authoritative risk checks.
- `src/RiskManagement/SessionRules.cs`: entry window and deterministic exits.
- `src/Execution`: only broker-facing order path.
