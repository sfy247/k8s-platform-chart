# Risk Management Rules

## Default Experimental Limits

- Strategy capital: $100
- Max notional per trade: $10
- Max concurrent positions: 2
- Max total exposure: $20
- Max daily loss: $3
- Max estimated loss per trade: $1

These are test controls, not claims of optimal profitability.

## Risk Engine Authority

The deterministic Risk Engine has final authority. An LLM may advise but cannot approve a trade.

## Required Checks

Before every order verify:
1. trading mode is PAPER
2. trading is enabled
3. market is open
4. entry window is active
5. symbol is allowed
6. quote is fresh
7. spread is acceptable
8. account state is available
9. position count is within limits
10. exposure is within limits
11. enough cash remains
12. daily loss limit is not breached
13. estimated per-trade loss is within limit
14. no duplicate/conflicting order exists
15. sell does not exceed owned long quantity

## Fail Closed

Reject new trades when configuration validation fails, broker state cannot be read, market clock cannot be read, market data is stale, local/broker positions materially disagree, order status is uncertain, the daily loss limit is reached, or the kill switch is active.
