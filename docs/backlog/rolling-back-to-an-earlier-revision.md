---
title: Rolling back to an earlier revision
status: deferred
area: web
related: [editing-a-transaction-in-the-dashboard]
---
**Wanted.** "Undo that correction" — restore the record as it was before the last change.

**Why it is not in Phase 2.** Cancel/restore covers the case that matters most; `transaction_revisions`
keeps every state so rollback needs no migration when it is built.
