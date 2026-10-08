---
title: Integrity checks at scale
status: deferred
area: persistence
since: 2026-10-03 (Phase 8a plan review; extended at the Phase 8a close)
---
**What.** The integrity checks run inside the health check's 5 s budget, which was never measured on a
large ledger, and `categorization_jobs` has no index on `transaction_id` alone that I-3 and I-4's job
subqueries could use. Each held receipt or slip also costs a few serial queries per run, because I-3 and
I-4 ask the receipt store's awaiting-confirmation predicate record by record (Codex review of the checks'
PR, #61: batch-load the predicate's inputs once and share them between I-3 and I-4). If a run grows slow the
tile shows "No answer within 5 s" — never a wrong finding.

Two more costs, from the Phase 8a closing review (Fable): a check scoped to one record — the explainer's
and a bug report's — still aggregates the whole ledger in I-1's queries before it keeps that record's rows,
so it costs about as much as a full run; and `bugs export` runs the checks again for every linked report it
writes, for the report's "findings now" — a full integrity pass per report, by the first point — so its time
grows with the ledger times the open reports.

**Why it is not done.** A personal ledger holds a few thousand records, the checks narrow candidates
before every correlated subquery, and the index would have put a second table into the phase's migrations.
The operator chose to wait for a measured slow run (the `HealthCheck` timing log shows it).

**What fixing it would take.** The index on `categorization_jobs (transaction_id)`; the predicate's inputs
loaded once per run; I-1's scope pushed into its inner aggregates rather than applied to their result; and
the export computing one full pass and handing each linked report its own record's findings from it.
