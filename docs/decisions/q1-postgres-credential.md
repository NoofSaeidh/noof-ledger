---
id: Q1
title: "Postgres credential: Windows-integrated auth (SSPI) or a DPAPI-protected password file?"
status: deferred
phase: Phase 1
related: [p1-2-postgres-credential-deferred]
---

**Default taken:** Neither — a generated password in a plaintext file outside the repo.

**Decide by:** Phase 1.

## Why the default is safe to defer

SSPI was never spiked. What shipped instead is a generated password written to a plaintext file at
`%LOCALAPPDATA%\NoofLedger\db.connection`, created by `ops/reset-database-auth.ps1` — outside the
repo and never in `appsettings.json`. That is acceptable for a single-user local dev machine, but it
is neither of the two options on the table, so the SSPI/DPAPI upgrade is re-parked here, decide by
Phase 1.
