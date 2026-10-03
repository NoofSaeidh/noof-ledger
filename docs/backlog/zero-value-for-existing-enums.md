---
title: Value 0 means unknown for the enums stored before Phase 8a
status: deferred
area: domain
since: Phase 8a (2026-10-03, operator's review of the contracts PR)
---
**What.** The rule now is that an enum's `0` is `Unknown` or `None`, so a default or missing value is never mistaken
for a real one. Phase 8a's enums follow it. The enums stored before it — a transaction's kind and status, a job's
kind and state, a revision's kind and others — still give `0` a real meaning. Moving them means shifting every stored
value in a data migration and every check constraint that names a number.

**Why it is not done.** The operator chose to apply the rule to new enums only and leave the existing ones for a
separate change.
