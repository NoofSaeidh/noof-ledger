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
- Creates the `noof_ledger` and `noof_ledger_test_template` databases if they don't
  already exist, and ensures the `pg_trgm` and `unaccent` extensions are
  installed in both.
- Restores `pg_hba.conf` to `scram-sha-256` authentication (in a `finally`
  block, so this happens even if a step above fails).
- Writes the resulting connection string to
  `%LOCALAPPDATA%\NoofLedger\db.connection` and sets it as the `NOOF_TEST_PG`
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

## Solution-wide accessibility sweep

`ops/inspect.ps1` runs `dotnet jb inspectcode` (`JetBrains.ReSharper.GlobalTools`,
pinned in `.config/dotnet-tools.json`) against `NoofLedger.slnx` and fails if any
finding under `src\` is ERROR-severity. It inspects `tests\` too and prints what
it finds there, but does not gate on it — requirement 1 of Phase 1C exempts
tests, and xUnit fixtures routinely trip `ClassCanBeSealed.Global` (67 of them,
last measured) for reasons the inspection cannot see; gating on a count that can
never reach zero would guard nothing. It catches what no Roslyn analyzer can: a
public **member** that nothing outside its own assembly ever calls
(`MemberCanBePrivate.Global`, `MemberCanBeInternal`), an unused public member or
type (`UnusedMember.Global`, `UnusedType.Global`), and a class with no
inheritors that could be sealed (`ClassCanBeSealed.Global`). Those five
inspections are raised from their default `SUGGESTION` to `ERROR` by the
committed `NoofLedger.sln.DotSettings`, which `jb inspectcode` picks up
automatically because it sits next to `NoofLedger.slnx` — the same file also
lights the same inspections up in Rider for anyone who opens the solution.

Run `dotnet build NoofLedger.slnx` first — `--no-build` means the script
inspects whatever was last built, not what is currently on disk. Then:

```powershell
pwsh -ExecutionPolicy Bypass -File ops/inspect.ps1
```

(Windows PowerShell 5.1's `powershell.exe` will refuse the script's
`#requires -Version 7`; use `pwsh`.) It writes `artifacts/inspect/report.xml`
(already `.gitignore`d) and prints every ERROR-severity finding under `src\` as
`file:line TypeId Message`, plus a one-line count of the ungated findings under
`tests\`, before failing on the `src\` count. `inspectcode` itself always exits
0, findings or not, so the script parses the report rather than trusting the
exit code.

This is a periodic sweep, not a per-build gate: a full solution-wide analysis
run takes roughly half a minute on this machine, and CI does not run it on
every push. Run it before closing an accessibility- or composition-focused
phase, or whenever a member's necessary visibility is in doubt. On a finding,
narrow the member (`private`, `private protected`, or `internal` — tests reach
narrowed `src` members through `InternalsVisibleTo`) or, if it is a genuine
exception, reject it in `.editorconfig` or with a file-local suppression that
states the reason — never by silently re-widening the member.
