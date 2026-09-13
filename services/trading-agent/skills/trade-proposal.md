# Skill: Trade Proposal

Convert validated intraday analysis into a structured BUY / SELL / HOLD proposal.

Required fields:
- symbol
- action
- proposed_notional
- entry_type
- strategy
- rationale
- invalidation
- target
- confidence
- timestamp

BUY may open/increase a long only. SELL may reduce/close an existing long only. Never propose short exposure. HOLD is preferred when evidence is weak.
