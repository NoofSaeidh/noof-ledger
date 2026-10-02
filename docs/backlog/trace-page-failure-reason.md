---
title: The trace page never shows why a record failed
status: deferred
area: web
since: 2026-10-02 (Phase 7 close)
related: [trace-page-exchange-slip]
---
**Found by the Phase 7 closing review (Fable, M-2); parked by the operator, 2026-10-02.**

**What is wrong.** `/transactions/{id}/trace` shows a record's status but never its `failure_reason`:
`TransactionSummary` carries the status only. Any failed record — a transfer that failed as `SameWallet`
or `MissingReceivedAmount`, an incomplete slip, a failure with no named reason — reads "Failed" on the
trace page, while the bot's echo for the same record names the reason.

**What fixing it would take.** `TransactionSummary` (`src/Noof.Ledger.Application/Diagnostics/TransactionTrace.cs`)
gains the reason, `EfTransactionTrace` reads it from `transactions.failure_reason`, and the trace page's
summary shows it beside the status when it is not `None` — one row, worded like the echo's reason.
