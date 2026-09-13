# Audit and Observability Rules

Record market observations, scan results, research decisions, trade proposals, risk decisions, order submissions, broker responses, fills, position updates, exits, daily lockouts, configuration errors, and reconciliation events.

Never log API secrets, full credentials, .env contents, or authentication headers.

Each decision record must be sufficient to reconstruct what the system knew, what it proposed, why risk approved/rejected it, what the broker did, and what happened afterward.
