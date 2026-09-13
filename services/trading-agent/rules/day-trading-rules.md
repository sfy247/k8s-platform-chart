# Day Trading Rules

## Session Rules

- Regular session only by default.
- Do not open new positions before the configured opening delay.
- Stop new entries at the configured entry cutoff.
- Flatten all positions before the end-of-day liquidation cutoff.
- No overnight positions.
- If the market is closed, new entries are prohibited.
- If the market clock cannot be verified, new entries are prohibited.

## Trade Eligibility

A symbol is eligible only when it is allowlisted, market data is fresh, spread is within limits, liquidity requirements are satisfied, required bars are present, there is no conflicting order, and portfolio/account limits are respected.

## Entry Rules

Every BUY proposal must include symbol, side, notional amount, strategy name, rationale, invalidation, target/exit condition, confidence, and timestamp.

Every proposal must be checked against position size, concurrent positions, portfolio exposure, cash reserve, daily loss, symbol allowlist, session time, data freshness, spread threshold, and duplicate-order rules.

## Exit Rules

Positions may be exited due to stop condition, target, strategy invalidation, portfolio risk, time-based exit, end-of-day flattening, or emergency kill switch.

Hard exits must not depend solely on an LLM.

## Prohibited Behavior

- no averaging down by default
- no revenge trading
- no martingale sizing
- no unbounded retries
- no pyramiding unless explicitly enabled and tested
- no unsupported symbols
- no trading on stale quotes
- no live trading from paper configuration
