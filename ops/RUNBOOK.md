# Ops Runbook

## PostgreSQL local setup (one-time)

`ops/reset-database-auth.ps1` is the one-time entry point for getting a local
PostgreSQL 18 instance ready for development and tests. Run it from an
elevated PowerShell:

```powershell
.\run.ps1 db-auth-reset
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

## After adding a migration

Any new EF Core migration must be applied to `noof_ledger_test_template` right after it builds, or
the E2E suite and `MoneyStorageTests` fail with a missing-column error the next time they clone it —
that failure is how you notice this step was skipped. `noof_ledger` itself is migrated only when the
operator starts the host (`Database:MigrateOnStartup`), never by hand.

```powershell
$admin = if ($env:NOOF_TEST_PG) { $env:NOOF_TEST_PG } else { (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection").Trim() }
$template = $admin -replace 'Database=postgres', 'Database=noof_ledger_test_template'
if ($template -notmatch 'Database=noof_ledger_test_template') { throw "Refusing: the connection string does not name the test template." }
$env:NOOF_LEDGER_EF_CONNECTION = $template
try {
    dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence
} finally {
    Remove-Item Env:\NOOF_LEDGER_EF_CONNECTION -ErrorAction SilentlyContinue
}
```

The template connection string carries the postgres password, so it goes to `dotnet ef` through the
`NOOF_LEDGER_EF_CONNECTION` environment variable - which `DesignTimeDbContextFactory` reads before
falling back to the normal resolution - never through `--connection`, which would put the password on
that process's command line. The output's last line must name the new migration. Do **not** run it
without pointing `dotnet ef` at the template one way or the other.

`.\run.ps1 update-test-template` runs exactly this (under the shared suite lock).

## Speech-to-text (Groq)

1. Create a key at console.groq.com (API Keys). The free tier needs no card.
2. In the Groq console, under Data Controls, turn on **Zero Data Retention**. Without it Groq may
   keep request logs up to 30 days for troubleshooting.
3. In the app, open Settings → Secrets, paste the key into **Groq API key**, save, and press
   **Test**. The test calls Groq's free `GET /models`.
4. Without a key the bot still acknowledges voice notes with `🎤 Transcribing…` and keeps them
   queued. They are transcribed once a key is saved. Nothing is lost and no attempt is spent.
5. Rate limits: 20 requests/minute on the free tier. A burst of notes is retried with backoff, not
   failed.

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

## Backups

`BackupWorker` runs inside the host, not as a separate process. On start it checks
`backup_runs` for the newest successful run; if there is none, or it is older than 24 hours, it
backs up immediately. Otherwise it wakes again when that success turns 24 hours old — not 24 hours
from whenever it happened to start — so a host that is not always on still backs up roughly once a
day rather than falling to every other day; a failed attempt is retried after 1 hour instead of
waiting for the next scheduled day.

**Where:** `%LOCALAPPDATA%\NoofLedger\backups\noof_ledger-yyyyMMdd-HHmmss.dump` (UTC timestamp in
the file name). Written under a `.tmp` name first and renamed only on success, so a half-written
dump never looks finished to anything that lists the directory.

**How many:** the newest 14. Older ones are deleted right after a successful backup, by file name
order (the timestamp in the name sorts the same as time, so no file needs to be opened to prune).

**Checking status:** every run — success or failure — is a row in `backup_runs`. The dashboard's
home page shows *Last backup: never* until the first success, then *Last backup: N min/h/d ago*
after one, or *Last backup: failed* whenever the most recent run failed (even after an earlier
success) — amber whenever nothing has ever succeeded, the last run failed, or the last success is
older than 36 hours. From a database connection directly:
```sql
SELECT started_at, finished_at, succeeded, file_name, size_bytes, error
FROM backup_runs ORDER BY started_at DESC LIMIT 5;
```

**Restoring a dump by hand** (into a *new* database — never over `noof_ledger` directly):
```powershell
$c = (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection" -Raw).Trim()
$p = @{}; foreach ($x in $c.Split(';')) { if ($x -match '^\s*([^=]+)=(.*)$') { $p[$Matches[1].Trim()] = $Matches[2].Trim() } }
$env:PGPASSWORD = $p['Password']
& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -h $p['Host'] -p $p['Port'] -U $p['Username'] -d postgres -c "CREATE DATABASE noof_ledger_restored"
& 'C:\Program Files\PostgreSQL\18\bin\pg_restore.exe' -h $p['Host'] -p $p['Port'] -U $p['Username'] -d noof_ledger_restored --no-owner --no-privileges `
    "$env:LOCALAPPDATA\NoofLedger\backups\<the .dump file>"
```
Drop `noof_ledger_restored` when you are done inspecting it — `psql ... -c "DROP DATABASE noof_ledger_restored"`.

A dump contains only the database — the data-protection key ring (`%LOCALAPPDATA%\NoofLedger\dp-keys`)
and the connection string (`%LOCALAPPDATA%\NoofLedger\db.connection`) live outside it. Secrets stored
in `app_secret` are encrypted with that key ring, so a dump restored on another machine, or after the
key ring is lost, has unreadable secrets until they are re-entered through the UI — copy the `dp-keys`
folder alongside the dump if a restore is meant to also carry the secrets forward.

**`ops/restore-check.ps1`** automates the check above and compares the result against a source
database instead of leaving that to your own eyes: it restores a dump (the newest one by default, or
`-DumpPath` for a specific one) into a throwaway scratch database, compares `wallet_balances` and
every ledger table's row count against `-SourceDatabase` (default `noof_ledger_test_template`),
prints the result, and drops the scratch database either way. It refuses to let the scratch target
ever be `noof_ledger`, `noof_ledger_test_template`, `postgres`, or either template database, by exact
name.

`backup_runs` is deliberately excluded from that row-count comparison (printed as an informational
line instead): `BackupWorker` writes the run's own `backup_runs` row *after* the dump finishes, so a
dump the worker made will always have one fewer `backup_runs` row than the live database it was taken
from — comparing it would report MISMATCH on every worker-made dump, by construction, not because
anything is wrong.

The comparison overall is against the database as it stands **right now**, not as it stood when the
dump was taken — anything written since (a captured message, a job row, a new backup run) shows up as
a difference against `-SourceDatabase`. Run it while the host is idle (no capture in flight), or point
`-DumpPath` at a dump you just took and compare before anything else writes to the database.

`Backup:Enabled=false` disables the worker (the test fixtures set it); `Backup:PgDumpPath` overrides
the `pg_dump.exe` binary location for the worker's own dumps, if it is not at the default
`C:\Program Files\PostgreSQL\18\bin\`. `restore-check.ps1` does not read that setting at all — it
takes its own `-PgRoot` parameter (default the same path) and finds `psql.exe`/`pg_restore.exe` under
it directly.

**The one real run against `noof_ledger`, per the operator's decision (B5):** this must be run by the
operator, or with the operator's explicit permission, since it reads the real ledger:
```powershell
pwsh -File ops/restore-check.ps1 -SourceDatabase noof_ledger
```
Record the result here once it has been run:

> _Not yet run. When it is: date, dump file name, and OK/MISMATCH go here._

## Logging and diagnostics

**Where logs live.** `%LOCALAPPDATA%\NoofLedger\logs\noof-ledger-<date>.log` by default — daily
rolling, 14 files kept (`Logging:File:RetainedFileCountLimit`, by file count only, never by days), 50 MB
cap each (`Logging:File:FileSizeLimitBytes`). To use a different folder, set `Logging:File:Directory` in
`appsettings.json` (or the `Logging__File__Directory` environment variable) to the path you want; the
app creates it if it does not exist. The file's own floor is `Logging:File:MinimumLevel` (`Debug` as
checked in) and the console's is `Logging:Console:MinimumLevel` (`Information`); `Serilog:MinimumLevel`
now holds only `Override` — a `Serilog:MinimumLevel:Default` left over from before this fails startup
fast, naming these two keys as its replacement. While PostgreSQL is reachable, the same events are also written
to the `app_log` table — every known secret (everything in `app_secret`, plus the database password)
is redacted to `***` before either sink sees a line. If the database is down, or the table sink itself
starts failing, the file is the only copy; nothing is lost, only the second copy is missing until the
sink recovers. Events logged before the database gate ever turns `Ready` are buffered in memory
(up to 10,000) and flushed to `app_log` the moment it does; if the gate never turns `Ready` before
shutdown, one warning line naming how many events never made it lands in the file/console instead —
they are still in the file itself, just never copied to the table.

**Reading `/diagnostics` and the trace page.** `/diagnostics` lists every health check — Database,
Migrations, Telegram, AI keys, Backup, Disk, Log sink — each with a "Logs" link that opens
`/diagnostics/logs` pre-filtered to that check's own log category. A check that throws or times out
shows only the exception type ("Check failed (ExceptionType) — see logs" or "No answer within 5 s"),
never the exception's message; the full exception is logged under the check's own category, which is
exactly what its "Logs" link opens. `/diagnostics/logs` is a paged,
filterable grid (level, time range, free text, source, transaction id) over `app_log`, reached from
the **Logs** tab; while the database is unavailable it shows the waiting banner like every other
page — read the file log with `.\run.ps1 logs` instead. Every transaction has its own page at
`/transactions/{id}/trace` — the message itself, its path from received to replied with timings and
the reason for any failure, and the `transaction_revisions` history below it; if the log rows have
aged out under retention the trace strip says so but the revision history still shows, since that
table is never pruned. Above Information (or Off), the database sink drops the stage events the
trace page reads, so the page shows an inline notice instead, linking to Log settings. Retention is
per level, set on `/diagnostics/logs/settings` (the **Log settings** link from the Logs page header)
and persisted in `app_setting`, never in `appsettings.json` — C# defaults: Verbose and Debug 1 day,
Information 30, Warning/Error/Fatal 90.

### Verbose logging

By default the database sink only records Information and above (per-level retention defaults:
Verbose/Debug 1 day, Information 30, Warning/Error/Fatal 90 — set on `/diagnostics/logs/settings`,
persisted in `app_setting`). To see Debug detail — model/Telegram/worker/health timings — temporarily:

1. Open `/diagnostics/logs` (the **Logs** tab), follow **Log settings**, set the database level to
   `Debug` (or `Verbose`) and click **Save** — nothing changes until you do. It survives a restart,
   and is kept for however many days that same screen's Debug retention field says (1 by default) —
   turn it back to `Information` when you are done, or let it age out.
2. Every Telegram message, model call and worker tick logs a Debug timing line
   (`{Operation} took {ElapsedMs} ms`) once the database level allows it; a call over its own slow
   threshold logs a Warning instead (`Logging:SlowOperationMs:<operation>`, e.g.
   `Logging__SlowOperationMs__model=45000` as an environment variable) and is recorded regardless of
   the database level. A Telegram long poll is judged against `Telegram:PollingSeconds` **plus** the
   `telegram` threshold, so raising the poll interval never turns an idle poll into a Warning.
3. Per-statement SQL is **not** part of step 1: the checked-in
   `Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore` is `Information`, and an override
   applies at the root logger, before any sink sees the event. To get SQL, set
   `Serilog__MinimumLevel__Override__Microsoft.EntityFrameworkCore.Database.Command=Debug` and
   restart. It then reaches the file (whose own floor is `Logging:File:MinimumLevel`, `Debug` as
   checked in) **and** `app_log` whenever step 1's level is `Debug` or lower — every query of every
   page and worker, so expect a large table for that day; remove the variable when done.
4. From a transaction's own trace page (`/transactions/{id}/trace`), the **Timings and debug log**
   link opens `/diagnostics/logs` pre-filtered to that transaction at Debug — the quickest way to see
   everything that happened to one message without hunting through the whole table.

Leave `appsettings.Development.json` alone — `.\run.ps1 start` always runs Production.

**Setting the PostgreSQL service to start automatically is the operator's decision, not the app's.**
The host waits indefinitely for PostgreSQL and needs no help to recover once it is up — but if you
want PostgreSQL itself to come back after a reboot without you starting it by hand, that is a Windows
service setting, from an elevated shell:

```powershell
Set-Service postgresql-x64-18 -StartupType Automatic
```

### Manual acceptance — Phase 5 (observability), the database going down and coming back

Ten minutes, no live model call, this costs nothing.

1. **Stop PostgreSQL:**
   ```powershell
   Stop-Service postgresql-x64-18
   ```
2. **Start the host** (`Push-Location .\publish; .\Noof.Ledger.Host.exe`, per the section below). The
   sign-in page — and every other page, if you are already signed in — shows a "Waiting for the
   database…" banner instead of an error or a crash. The process keeps running.
3. **Check the log file** under `%LOCALAPPDATA%\NoofLedger\logs`: it records each connection attempt
   with the 2 s → 4 s → 8 s → 16 s → 30 s backoff (the first ten attempts each logged, then every
   tenth).
4. **Start PostgreSQL again:**
   ```powershell
   Start-Service postgresql-x64-18
   ```
   The host migrates and comes up on its own — no restart needed. Reload the sign-in page; the banner
   is gone.
5. **Ask the bot for its own health:** send `/health` from the owner's chat. Expect a plain-text
   summary — `Health: all good` or a line per check that is not Ok.

## run.ps1 — the one entry point

`run.ps1` in the repo root is how the app is launched and operated from here on; `.\run.ps1` (or
`.\run.ps1 help`) prints the command table, and `.\run.ps1 help <command>` prints one command's full
detail — prerequisites included. `Get-Help .\run.ps1 -Full` works too. One line each:

- `start [-Dev]` — the main way to run it: `dotnet run` in Production, same behaviour as the
  published exe; `-Dev` for the Development launch profile in an IDE.
- `publish [-Output <dir>]` — build, test, publish (`ops/publish.ps1`), under the shared suite lock;
  refuses to publish on a failed or empty test run.
- `start-published [-Path <dir>]` — run a published `Noof.Ledger.Host.exe`, from anywhere.
- `set-password <username>` — create or reset a login; the only way a user is ever created.
- `test [fast|db|e2e|all] [-Filter <class>]` — fast needs no database (it excludes the Host.Tests
  classes tagged `[Trait("Category", "Database")]`); db/e2e/all take the shared suite lock, and db
  runs those tagged classes too.
- `update-test-template` — apply the newest migration to `noof_ledger_test_template` (see below).
- `clean-test-dbs [-WhatIf]` — drop leftover `noof_test_*`/`noof_e2e_*` databases.
- `restore-check [args passthrough]` — forwards to `ops/restore-check.ps1`.
- `db-auth-reset` — one-time PostgreSQL setup (`ops/reset-database-auth.ps1`, needs an elevated shell).
- `pg start|stop|status` — the `postgresql-x64-18` Windows service (start/stop need admin).
- `status` — PostgreSQL, `/healthz`, and the newest log file, on one screen.
- `logs [-Tail <n>] [-Follow]` — open, tail, or follow the log directory.
- `backups` — open the backup directory.
- `inspect` — the solution-wide accessibility sweep (`ops/inspect.ps1`).

Every `ops/*.ps1` script named above still exists and still works stand-alone; `run.ps1` is what
calls it with the right arguments, not a replacement for it.

**Changing the port.** Edit `Urls` in the published `appsettings.json` (or pass `--urls` on the
command line, which always wins) - `ASPNETCORE_URLS` is overridden by it, because
`WebApplication.CreateBuilder` adds the JSON config source after the `ASPNETCORE_`-prefixed
environment source, so the file wins over the standard ASP.NET Core environment-variable way of
re-pointing an app.

**Why the current directory used to matter, and no longer does.** Before Task 11,
`WebApplication.CreateBuilder` derived the content root from the process's **current directory**, not
from the executable's location, so a host launched from anywhere but its own install directory
resolved every static asset against the wrong folder — and `MapStaticAssets` answered each one **200
with an empty body** rather than 404: the app came up, every page rendered, nothing was logged, and
the whole thing was unstyled with no Blazor script. It looked exactly like a CSS bug, and cost two
separate debugging sessions. `Program.cs` now sets the content root from
`AppContext.BaseDirectory` instead, so `run.ps1 start-published` (and a real deployment) works from
any current directory.

## Manual acceptance — the end-to-end check no test can do

The automated suite never talks to Telegram or to Anthropic. Everything between
"a person types a message" and "a figure appears on the dashboard" is verified
here, by hand, once per phase. Budget ten minutes.

**It costs real money** — a few tenths of a cent. Step 5 makes one live call to
Claude Haiku. That is the point of the exercise; the eight tests that would do
it automatically stay skipped precisely so a test run never spends anything.

### Before you start

```powershell
.\run.ps1 publish            # refuses to publish if a single test fails
```

Then confirm the database is in the state you think it is:

```powershell
$c = (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection" -Raw).Trim()
$p = @{}; foreach ($x in $c.Split(';')) { if ($x -match '^\s*([^=]+)=(.*)$') { $p[$Matches[1].Trim()] = $Matches[2].Trim() } }
$env:PGPASSWORD = $p['Password']
& 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -h $p['Host'] -p $p['Port'] -U $p['Username'] -d noof_ledger -c `
  "SELECT (SELECT count(*) FROM app_user) users, (SELECT count(*) FROM app_secret) secrets, (SELECT count(*) FROM transactions) txns, (SELECT count(*) FROM wallets) wallets, (SELECT count(*) FROM categories) cats;"
```

A freshly recreated ledger reads `0 | 0 | 0 | 1 | 20`.

### The run

1. **Start it.** `.\run.ps1 start-published`. It binds loopback only and refuses to
   start otherwise. Open the address it prints.

   Authentication is always on. The first time, `noof_ledger` has **no user
   row**, so create one with `.\publish\Noof.Ledger.Host.exe user set-password
   noof` and sign in through the browser with that username and password. The
   cookie it sets is what lets the rest of this checklist reload pages without
   signing in again.

2. **The dashboard answers.** `/` shows an empty month. Stop PostgreSQL and
   reload: it must say it cannot reach the database, **not** return a 500 (the
   cookie from step 1 is already valid, so this reload never touches the
   database for authentication — only for the page's own data). Start
   PostgreSQL again.

3. **Paste the secrets** at `/settings/secrets`: the Telegram bot token from
   BotFather, and an Anthropic API key. Press **Test** beside the key — it calls
   the models endpoint, which costs nothing, and says plainly whether the key
   works. Neither value is ever echoed back to the page.

4. **Claim the bot.** Message your bot once from Telegram. The first chat that
   writes becomes the owner; every other chat is rejected from then on, before
   its text is even read. The poller picks the token up within seconds — no
   restart.

5. **Send `кофе 250 рсд`.** Expect, in order:
   - an immediate reply, *"Saved. I'll add the amount once it's categorised."*;
   - within a few seconds, **that same message edited in place** to show 250 RSD,
     a category, and the wallet.

6. **Check the figure came from your text, not from the model.** Send
   `такси 1 500 рсд`. It must read 1500, not 500 — the amount is verified
   character-for-character against what you typed, and a space is a number
   boundary. This is the check that failed in Phase 1B's closing review.

7. **Check a message with no currency.** Send `обед 700`. It must be recorded as
   RSD, the single hard default. Choosing it from Telegram is deferred; see
   `docs/BACKLOG.md`.

8. **The dashboard shows it.** Reload `/`. Both spends appear with this month's
   totals per currency, never summed across currencies.

### Afterwards

Confirm the write-once guarantee is real rather than promised — this one was
missing from the live database for a whole phase before anybody checked:

```sql
SELECT tgname FROM pg_trigger
WHERE tgrelid = 'public.merchant_aliases'::regclass AND NOT tgisinternal;
```

Both `merchant_aliases_write_once_guard` and `merchant_aliases_no_truncate` must
be listed. One alone means the database predates the migration that adds the
second, and `TRUNCATE merchant_aliases` would succeed.

### If something goes wrong

- **No reply at all** — the bot token is wrong, or another process is polling the
  same bot. Telegram gives updates to one poller.
- **"Saved" but never edited** — the worker could not reach the model. Check the
  console for an account-level failure (401/402/403); it stops claiming new jobs
  for five minutes rather than burning the whole backlog's retry budget.
- **Nothing at all after the machine was off** — Telegram discards unfetched
  updates after 24 hours and a bot cannot read history. That is a property of the
  platform, not a bug here; four researched options are costed in
  `docs/BACKLOG.md`, none built, by decision.
- **Leftover `noof_test_*` databases after a killed test run** —
  `pwsh -File ops/clean-test-databases.ps1`.
