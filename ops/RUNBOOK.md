# Ops Runbook

## PostgreSQL local setup (one-time)

`ops/reset-database-auth.ps1` is the one-time entry point for getting a local
PostgreSQL 18 instance ready for development and tests. Run it from an
elevated PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File ops/reset-database-auth.ps1
```

What it does:

- Temporarily switches loopback (`127.0.0.1`/`::1`) authentication in
  `pg_hba.conf` to `trust` so it can set a password without already knowing one.
- Generates a random password and sets it on the `postgres` superuser.
- Creates the `noof_finance` and `noof_test_template` databases if they don't
  already exist, and ensures the `pg_trgm` and `unaccent` extensions are
  installed in both.
- Restores `pg_hba.conf` to `scram-sha-256` authentication (in a `finally`
  block, so this happens even if a step above fails).
- Writes the resulting connection string to
  `%LOCALAPPDATA%\NoofFinance\db.connection` and sets it as the `NOOF_TEST_PG`
  user environment variable.

Backups: before editing `pg_hba.conf`, the script copies it to
`pg_hba.conf.backup-<timestamp>` in the same PostgreSQL data directory
(`C:\Program Files\PostgreSQL\18\data` by default). If the script fails
partway through, restore the newest of these backups and restart the
`postgresql-x64-18` service.

Idempotency: safe to re-run. It reuses existing databases instead of failing
if they're already present, always resets the password and credential file to
a fresh value, and always restores password authentication in its `finally`
block regardless of how it exits.

Consuming the connection string: `Noof.Ledger.TestKit.DatabaseSettings` reads
`NOOF_TEST_PG` first, then falls back to the `db.connection` file described
above (because a `User`-scope environment variable is not inherited by shells
that were already running when the script set it). Neither the connection
string nor the file's contents should ever be logged, committed, or printed —
this is a public repository.
