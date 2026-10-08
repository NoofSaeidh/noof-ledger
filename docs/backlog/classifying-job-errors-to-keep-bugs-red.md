---
title: Classify job errors so an unexpected exception stays a Bug
status: deferred
area: persistence
since: 2026-10-02 (Phase 8a, IR-10)
related: [failed-job-on-a-cancelled-record, atomic-job-and-record-failure]
---
**What.** The integrity checks put a record with a failed job under *Waiting on you* (I-4), never under
*Bugs*: most job failures are an outage, a missing key or an unreadable photo, and the bot has already
said so. An exception from our own code that fails a job is therefore amber, not red.

**Why it is not done.** Telling the two apart needs the queue to record what kind of failure ended a job
— a column on `categorization_jobs` and a migration, plus a classification at every worker's catch. The
operator chose not to now (spec IR-10); the failed job's last error is already shown on the integrity
page and in a bug report, so a real bug is still visible, only not red.
