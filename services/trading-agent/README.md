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
