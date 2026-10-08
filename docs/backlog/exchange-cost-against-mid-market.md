---
title: What exchanges cost against the mid-market rate
status: deferred
area: other
since: Phase 8b (2026-10-08)
---
**Wanted.** How much an exchange lost against the mid-market rate of its day — in a month's summary
as a line of its own, and across venues, to see which exchange office or bank gives the better rate.

**Why it is not scheduled.** Phase 8b counts only explicit fees as spending and ignores the spread
(SS-17 in `docs/specs/2026-10-08-spending-summary-design.md`): an exchange's two legs already hold
what each wallet moved, and the operator chose not to report the difference in 8b. The rate archive
8b adds (`fx_rates`) is what this would read.
