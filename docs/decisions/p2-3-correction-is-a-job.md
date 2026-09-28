---
id: P2-3
title: "A correction is a job, not a table"
status: decided
date: 2026-09-22
phase: Phase 2
---

`categorization_jobs` gained `kind`, `instruction` and `source_message_id` instead of a separate
corrections table, because a correction needs exactly the claim/lease/retry/attempt-cap machinery the
queue already has. The cost is an ordering rule in `ClaimAsync`: a job is never claimed while an earlier
job for the same transaction is Pending or Claimed, or a correction could be applied and then
overwritten by the reading it corrected. A failed correction never marks a transaction Failed.
