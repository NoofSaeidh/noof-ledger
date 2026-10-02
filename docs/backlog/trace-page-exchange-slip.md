---
title: The trace page shows an exchange slip as a fiscal receipt
status: deferred
area: web
since: 2026-10-02 (Phase 7 close)
---
**Operator, 2026-10-02:** parked at the Phase 7 close rather than fixed in the phase.

**What is wrong.** `/transactions/{id}/trace` renders a record's exchange slip through the fiscal
receipt's section — *Shop*, *Fiscal number* and an empty line-items table — and shows none of what a
slip holds: its slip number, the amount given, the amount received, the printed rate. A held slip not yet
recorded (still `Captured`, awaiting "Record anyway") shows Kind "Expense", the default a captured record
holds, rather than an exchange waiting for confirmation.

**Also from the Phase 7 closing review (Fable, M-2).** The trace page never shows a record's
`failure_reason`: its summary carries the status only, so a failed exchange or an incomplete slip reads
"Failed" with no reason, while the bot's echo names it. And a slip's receipt section shows
`receipts.total`, which reads 0.00 RSD when the dinar side was unread (A-11) — a figure the slip never
printed.

**What fixing it would take.** Web only: the data is already in `ExchangeSlipView`
(`src/Noof.Ledger.Application/Receipts/IReceiptStore.cs`), so the trace page needs a slip section of its
own beside the fiscal one, a held slip's kind shown as the exchange it will be, and the failure reason
in the summary (`TransactionSummary` gains it from `transactions.failure_reason`). The screenshots
`docs/screenshots/app/trace-slip-check-*.png` show the held case today.
