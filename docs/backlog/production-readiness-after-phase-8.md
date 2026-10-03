---
title: Production readiness after Phase 8
status: deferred
area: ops
since: 2026-10-03
related: [deferred-from-phase-4-money-model-and-backup]
---
**Wanted.** Once Phase 8 is done, make `noof_ledger` the database the operator actually records
spending in. Three parts:

**1. Clean the database.** Delete everything in `noof_ledger` except the stored secrets — every record
captured while trying the app out goes, so real use starts from an empty ledger without re-entering
the model, speech and Telegram credentials. Reference data the migrations seed stays with the schema.

**2. Production on its own PostgreSQL server, out of agents' reach.** Today `noof_ledger` shares a
server with every test clone and the test templates, so an agent running tests is one connection
string away from the operator's real data. Production moves to a server the tests never connect to.
This settles the "separate PostgreSQL instance for tests" item in
`deferred-from-phase-4-money-model-and-backup`, which the operator had left open. A separate server
alone is not the boundary: agents run as the same Windows user that can read
`%LOCALAPPDATA%\NoofLedger\db.connection`, so how the production credential is kept from them is
part of this work.

**3. Backups.** `BackupWorker` already dumps `noof_ledger` locally. What production needs beyond that —
a copy off the machine (Q8, `docs/decisions/q8-onedrive-backup-scope.md`), encrypted dumps, a tested
restore — is left for the operator to decide when this is scheduled.
