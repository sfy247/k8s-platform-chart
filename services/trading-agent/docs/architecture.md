# Day Trading Architecture

```text
Market Clock
    |
    v
Market Data ---> Scanner
                   |
                   v
          Market Research Agent
                   |
                   v
            Day Trader Agent
                   |
                   v
          Trade Proposal Record
                   |
                   v
        Deterministic Risk Engine
             |              |
          REJECT          APPROVE
             |              |
             v              v
           Audit       Execution Service
                              |
                              v
                         Alpaca PAPER
                              |
                              v
                       Position Manager
                              |
                              v
                      End-of-Day Flatten
```

Principle: LLMs interpret and propose. Deterministic code enforces safety and broker state.
