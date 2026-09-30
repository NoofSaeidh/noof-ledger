---
title: Transfers in transit — leaving one wallet days before arriving in another
status: deferred
area: persistence
since: 2026-10-01 (Phase 7 brainstorming)
---
**Operator, 2026-10-01:** not frequent enough to model in Phase 7; one date per transfer, with this
recorded for later.

**The case.** A bank or international transfer, often with a conversion, leaves the source wallet on
one day and reaches the destination days later. Phase 7 gives a transfer a single `occurred_on`, so
both legs post on the same day: the destination shows the money before the bank does, and a balance
statement on the destination taken in between records an adjustment that the arrival then reverses.
The operator can date the whole transfer by its arrival day instead; the source then shows the money
for a few days too long. Either way the error is a few days wide and the next balance statement
settles it.

**Shapes considered, none chosen:**
- **A date per leg** — `transfers` gains the arrival date (null while pending); the balance view
  orders each entry by its own leg's date rather than the transaction's. A later message ("пришли
  деньги на райф", possibly with the received amount of a conversion) closes the pending leg.
- **An in-transit wallet per currency** — a hidden technical wallet the send credits and the arrival
  debits, as a clearing account does in accounting. No per-leg dates, but two transactions and a
  wallet the operator never asked for.

**Not the same thing as cash held between two transfers.** Withdrawing cash on one day and depositing
some of it (or its exchange) weeks later is already modelled by Phase 7: each step is its own
same-day transfer and the cash wallet holds the money in between.
