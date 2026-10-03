---
title: A dashboard page for the background services
status: deferred
area: web
since: Phase 8a (2026-10-03, operator's review of the bot PR)
---
**What.** The host runs several background services (categorisation, receipts, exchanges, backup, log retention, the
bug-report explanation worker). Nothing shows their state in one place: whether each is running, waiting for the
database, cooling down after an account refusal, its last tick and its last error. The operator wants a dashboard page
for them.

**Why it is not done.** A new page and a way for each worker to report its state — its own feature.
