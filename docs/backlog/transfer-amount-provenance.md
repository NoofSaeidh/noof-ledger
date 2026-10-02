---
title: Remember whether a transfer's received amount was said or worked out
status: deferred
area: ledger
---
**Wanted.** A transfer records whether its destination amount was stated by the operator or worked
out by the ledger from the stated rate, so a correction can tell the two apart.

**Why it is not scheduled.** `transfers` stores only the settled amounts and the stated rate. A
correction infers a "worked out by the ledger" side from the arithmetic instead (spec A-21): the
stated rate applied to the source principal giving exactly the destination principal. The cases it
cannot tell apart are the ones where the figures agree — *"100 EUR for 11700 RSD at 117"* reads
the same as *"100 EUR at 117"*, so a later *"the rate was 118"* re-derives 11800 instead of keeping
the 11700 that was said. The echo shows the result and a second correction fixes it (P2-1). Raised by
the Codex review of PR #32; the operator chose the backlog over a column in `AddTransfersAndExchange`.
Doing it later means a new migration (a nullable flag on `transfers`, carried through `TransferFacts`
and `TransferView` into the correction rendering).
