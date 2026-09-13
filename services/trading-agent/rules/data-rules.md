# Market Data Rules

New entries require market data newer than the configured freshness threshold.

Required fields include latest price/trade, bid, ask, timestamp, recent bars, and volume where required.

Reject or HOLD when bid/ask are invalid, ask < bid, timestamp is missing, data is stale, required bars are missing, or spread exceeds the configured threshold.

Agents must never fabricate missing prices, indicators, bars, or timestamps.
