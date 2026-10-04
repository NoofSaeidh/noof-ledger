---
title: PostgreSQL-specific code in its own project
status: deferred
area: persistence
since: Phase 8a (2026-10-03, operator's review of the report-storage PR)
---
**What.** Code that names the database provider — Npgsql types, PostgreSQL error codes such as `23505`, raw SQL
written for PostgreSQL, `jsonb` and identity-column configuration — sits in `Noof.Ledger.Persistence` beside the
provider-neutral EF code (for example `EfBugReportStore` recognising a unique-index violation by its PostgreSQL code).
The operator wants every provider reference in one separate project, so the other projects stay generic.

**Why it is not done.** It is a cross-cutting move of existing Persistence code, not part of any feature; it belongs
in its own mechanical PR.
