---
title: Editing a transaction in the dashboard
status: deferred
area: web
related: [rolling-back-to-an-earlier-revision]
---
**Wanted.** Change the amount, date, category or merchant of a record from the web UI, or cancel it
there.

**Why it is not in Phase 2.** Phase 2 makes Telegram the place to correct a record — reply, the
Изменить button, or editing the original message — because that is where the operator already is when
the echo arrives. The dashboard edit is a second surface over the same revision history.

**Cost already paid.** `transaction_revisions` records every state, and `CategorizationAuthority.User`
already outranks the model, so a dashboard edit is a page plus a revision row, not a schema change.
