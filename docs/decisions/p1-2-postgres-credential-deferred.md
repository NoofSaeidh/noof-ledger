---
id: P1-2
title: "Q1, the PostgreSQL credential (was due this phase)"
status: deferred
date: 2026-09-21
phase: Phase 1
related: [q1-postgres-credential]
---

**Decision:** Leave as is. Re-parked to a new hardening phase, by explicit instruction.

## Q1 is deferred, deliberately, and a new phase is owed

The user's words: *"Оставь как есть до последней фазы (нужны новая фаза почистить и подготовить к
'продакшну' руками)."* So Q1 does not move in Phase 1, and a **hardening / production-readiness
phase** is now owed at the end of the roadmap. It should collect at least: the DPAPI-or-SSPI
credential decision, a real look at what is logged, and whatever else accumulates as "fine for a
single-user dev machine".

Separately, and NOT part of that deferral: `ops/reset-database-auth.ps1:76` writes
`Include Error Detail=true` into the credential file, so it reaches the application's runtime
connection string and puts **parameter values into Npgsql exception text**. That is a secrets-leak
surface and Phase 1 is when secrets start flowing through EF. Strip it from the runtime string in
Phase 1; it is unrelated to how the password is stored.
