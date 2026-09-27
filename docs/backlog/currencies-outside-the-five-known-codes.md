---
title: Currencies outside the five known codes
status: deferred
area: ai
since: 2026-09-23
---
**Wanted.** A sixth currency code, or at least a clean failure when one is meant, instead of the
silent RSD default.

**Why it is not scheduled.** `record_spending`'s (renamed to `record_transaction` in Phase 4) response schema constrains `currency` to a
compile-time enum of the five supported codes, so the model has no way to answer with a sixth even
when a message names one — it lands on `CategorizationWorkerOptions.DefaultCurrency` (RSD) instead,
visible only in the echo if the operator happens to notice the wrong code. Widening it safely is
Phase 4/7 work: `CurrencyCode.Supported` conflates "nameable" (can appear as an ISO code at all) with
"rateable" (has an exchange-rate source), and Phase 7's rate source (`docs/OPEN-QUESTIONS.md` Q4) is
scoped to the same five. Recorded by the operator's 2026-09-23 review (P2-5, D-F).

**What it costs, when built.** Split `CurrencyCode.Supported` into a nameable set and a rateable
subset, widen the schema enum to the nameable set, and reopen Q4 for whichever currencies gain a rate
source.
