# Execution Rules

## Broker Isolation

Only the execution service may read Alpaca API credentials. Agents must never receive keys or secrets.

## Environment Rules

- Validate the paper endpoint explicitly.
- Reject a live endpoint when in paper mode.
- Keep paper and live credentials separate.
- No automatic paper-to-live fallback.

## Order Submission

Before submission require a risk approval record, unique client_order_id, duplicate check, and validation of symbol/side/amount/time-in-force.

## Retry Rules

Retries must be idempotent.

If submission status is unclear:
1. query broker by client_order_id
2. reconcile existing order state
3. submit again only if absence is confirmed

Never blindly retry an order submission.

## Fill Handling

Record broker order ID, client order ID, status, submitted time, filled time, filled quantity, average fill price, rejection reason, and cancellation reason.
