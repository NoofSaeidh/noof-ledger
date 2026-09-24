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

## After adding a migration

Any new EF Core migration must be applied to `noof_ledger_test_template` right after it builds, or
the E2E suite and `MoneyStorageTests` fail with a missing-column error the next time they clone it —
that failure is how you notice this step was skipped. `noof_ledger` itself is migrated only when the
operator starts the host (`Database:MigrateOnStartup`), never by hand.

```powershell
$admin = if ($env:NOOF_TEST_PG) { $env:NOOF_TEST_PG } else { (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection").Trim() }
$template = $admin -replace 'Database=postgres', 'Database=noof_ledger_test_template'
if ($template -notmatch 'Database=noof_ledger_test_template') { throw "Refusing: the connection string does not name the test template." }
dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --connection $template
```

The output's last line must name the new migration. Do **not** run it without `--connection`.

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

## Start the published app from its own directory

```powershell
Push-Location .\publish; .\Noof.Ledger.Host.exe; Pop-Location
```

The `Push-Location` is not decoration. `WebApplication.CreateBuilder` derives the content root from
the process's **current directory**, not from the executable's location, so a host launched from
anywhere else resolves every static asset against the wrong folder — and `MapStaticAssets` then
answers each one **200 with an empty body** rather than 404. The app comes up, every page renders,
nothing is logged, and the whole thing is unstyled with no Blazor script. It looks exactly like a
CSS bug, and it has now cost two separate debugging sessions.

A real deployment always launches with its working directory set to its install directory, which is
why `HostProcess` in the E2E fixture sets it too.

## Manual acceptance — the end-to-end check no test can do

The automated suite never talks to Telegram or to Anthropic. Everything between
"a person types a message" and "a figure appears on the dashboard" is verified
here, by hand, once per phase. Budget ten minutes.

**It costs real money** — a few tenths of a cent. Step 5 makes one live call to
Claude Haiku. That is the point of the exercise; the eight tests that would do
it automatically stay skipped precisely so a test run never spends anything.

### Before you start

```powershell
pwsh -File ops/publish.ps1            # refuses to publish if a single test fails
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

1. **Start it.** `Push-Location .\publish; .\Noof.Ledger.Host.exe` — from that
   directory, per the section above. It binds loopback only and refuses to
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
