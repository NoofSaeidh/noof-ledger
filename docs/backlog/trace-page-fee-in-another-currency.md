---
title: The trace page cannot render a transfer whose fee is in another currency than its leg
status: deferred
area: web
since: 2026-10-08 (Phase 8a close, Fable review)
related: [trace-page-exchange-slip, trace-page-failure-reason, integrity-checks-at-scale]
---
**What.** A transfer's `Fee` line in a currency other than its fee leg's wallet is an I-2 finding since the
Phase 8a close, and every finding links to its record's trace page. That page reads the transfer through
`TransferLines.RateOf`, which takes the fee out of its leg (`from - fee`, `to + fee`) to price the exchange; for a
cross-currency transfer without a stated rate, a fee in another currency throws `CurrencyMismatchException`,
and `/transactions/{id}/trace` fails to render. So the *Trace* link on exactly the finding that names this fault —
on `/diagnostics/integrity`, and on a bug report about the record — opens a page that cannot show it.

**Why it is not done.** The write path does not produce such a record (I-2 is there to say so if it ever does), and
a bug report about one is still readable: it reads its revisions straight from `transaction_revisions`, not
through the trace reader (spec P-23). The trace reader is Phase 5/7 code outside the phase's scope.

**What fixing it would take.** The trace reader shows such a transfer without a rate — the legs and the fee as
stored — instead of throwing: `RateOf` (or its caller in the trace reader) returns no rate when the fee's currency
is not its leg's, with a test seeding the I-2 fault the way `FactsMismatchKindCheckTests` does.
