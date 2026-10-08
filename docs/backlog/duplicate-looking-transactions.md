---
title: Flagging duplicate-looking transactions
status: deferred
area: persistence
since: Phase 8a design (2026-10-02)
---
**Wanted.** The same spending captured twice — a voice note and a text about one purchase, or a
receipt photo plus a typed note — is not caught today. The database blocks only Telegram redelivery,
a fiscal receipt seen twice and a slip seen twice. A check would self-join completed expenses on the
same wallet with the same principal sum per currency, dated within a day of each other.

**Why it is not scheduled.** Two coffees on one day look the same, so the check needs a way to say
"not a duplicate" and remember the rejected pair permanently (the original design's §11 rule for the
merchant merge inbox) — a table, a migration and a dashboard action. Without it the finding would
recur forever and the integrity page would become wallpaper. Phase 8a ships its four checks without
it (`docs/specs/2026-10-02-integrity-and-bug-reports-design.md`).
