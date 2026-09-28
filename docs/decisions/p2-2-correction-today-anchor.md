---
id: P2-2
title: "A correction's \"today\" is the reply's own send day, not the original message's"
status: decided
date: 2026-09-23
phase: Phase 2
---

The plan (line 1867) fed every job `Today = sub.SentOn`, the day the original message was captured.
A per-task review flagged that a Correct job needs the reply's own day instead, and the plan left it
deferred rather than deciding it — a gap, not a decision, caught by the closing review
(2026-09-23). Left as written, a purchase captured Monday corrected on Wednesday with *"это было
позавчера"* resolves позавчера from Monday, not Wednesday, and every relative word in a correction
is off by however long the correction waited.

**Resolution:** `categorization_jobs.instruction_day` (nullable `date`, migration `AddInstructionDay`)
stores the correction reply's own local day, computed by `EfRecordEditor.RequestCorrectionAsync` from
the reply's `sentAt` and the transaction's `time_zone_id`. `CategorizationWorker` reads
`job.InstructionDay ?? sub.SentOn` as `Today`, so a first reading and a Reinterpret job (no
`InstructionDay`) are unaffected and keep D2's rule; only a Correct job's anchor moves.
