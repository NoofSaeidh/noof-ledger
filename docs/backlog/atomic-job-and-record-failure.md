---
title: Fail a job and its record in one commit
status: deferred
area: persistence
since: 2026-10-02 (Phase 7 close)
---
**Operator, 2026-10-02:** parked at the Phase 7 close; raised by the Codex review of PR #49.

**The gap.** Every worker that ends a reading in failure — `CategorizationWorker`, `ExtractReceiptWorker`,
`ReceiptCategorizationWorker`, `TranscriptionWorker` and `RecordExchangeWorker` — fails the job
(`IJobQueue.FailAsync`) and then marks the record failed (`MarkFailedAsync`), in two separate commits. A
database failure between the two leaves a `Captured` record with no job left to claim it and no failure
echo: the bot's "Recording…" acknowledgement is never replaced. Nothing is lost — the record and its
message are still there, and a reply to it queues a correction — and the window is the time between two
statements against a local database, which is why it was parked rather than fixed.

**What closing it would take.** One persistence operation that fails the job and marks the `Captured`
record failed, with its reason, in the same database transaction, used by all five workers in place of
the two calls; the failure echo then follows as today.
