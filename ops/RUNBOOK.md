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
inspections are raised from their default `SUGGESTION` to `ERROR` by
`ops/inspect.DotSettings`, which the script passes with `-s`.

That file is deliberately **not** called `NoofLedger.sln.DotSettings`. That
name is the solution's shared settings layer, which Rider reads too, and
raising these five inspections there turned every test project red over 92
findings that requirement 1 exempts and nobody intends to fix — 67 of them
`ClassCanBeSealed.Global` against xUnit fixtures. The severity floor exists so
`-e=WARNING` reports these inspections at all; it belongs to the gate, not to
everyone's editor. In Rider they stay at ReSharper's defaults and still show
up as hints where they apply.

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

## Leftover test databases

`Noof.Ledger.Persistence.Tests` creates one throwaway PostgreSQL database per test
(`noof_test_*`) and `Noof.Ledger.E2E.Tests` one per host fixture (`noof_e2e_*`).
Both drop them when the run ends. A run killed partway through, or a `DROP` that
times out waiting on a Postgres checkpoint, leaves them behind — 166 had
accumulated before anyone counted.

```powershell
pwsh -File ops/clean-test-databases.ps1          # drop them
pwsh -File ops/clean-test-databases.ps1 -WhatIf  # list them and drop nothing
```

The script refuses `noof_ledger` and `noof_ledger_test_template` **by name**, not
merely by the pattern it searches with. A pattern is a filter; a refusal is a
guarantee, and this script's whole job is dropping databases.

Two bugs that used to turn a single slow `DROP` into dozens of orphans are fixed
in `PostgresFixture`: the list of created databases was a plain `List<string>`
mutated from parallel tests, and one failed drop used to abort the loop and skip
every remaining database. Teardown now continues past a failure and ends by
naming what it could not remove, pointing here.
