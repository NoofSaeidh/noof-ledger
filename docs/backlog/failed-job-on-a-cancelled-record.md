---
title: A job that fails while its record is cancelled leaves no reason, and Restore strands it
status: deferred
area: persistence
since: 2026-10-02 (Phase 7 close)
related: [atomic-job-and-record-failure]
---
**Found by the opus review of the Phase 7 closing fixes** (Codex was at its usage limit for those PRs).

**The gap.** `MarkFailedAsync` changes only a record that is still `Captured` (spec A-22), so a job that
fails while its record is `Cancelled` fails the job and leaves the record's `failure_reason` at `None`.
Restore then brings the record back `Captured`, with no job left to claim it and no buttons on its echo.
Two ways in: `RecordExchangeWorker` failing a slip (`LegCurrencyMismatch`, say) after the slip was
queued for recording, or "Record anyway" was pressed, and the operator then pressed Cancel; and an
`ExtractReceipt` job ending unreadable, or out of retries, after a Cancel. A reply to the record still
recovers it, as an ordinary correction.

**Why it is not fixed.** A-31 closed the one case the closing review found — an incomplete slip saved
while its photo was cancelled. The general fix needs design: widening `MarkFailedAsync` to `Cancelled`
records would also stamp reasons on records that were `Completed` before they were cancelled, which
Restore must bring back `Completed`. A failure would have to remember that the record never got past
`Captured`, whatever its status is now.
