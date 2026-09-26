# Demo database and screenshots (design)

**Status:** approved in conversation 2026-09-26; this written spec awaits the operator's review before
an implementation plan is written. Branch `demo-environment`, cut from `master` (67225ea). Replaces a
larger first draft (a live fake Telegram chat, a rule-based fake model, a demo tool running the app
in-process), which the operator cut back to what is below.

## Goal

The operator can see how the app and the bot look without configuring anything, and every visible
change leaves a trace in git:

1. **A demo database that lives on.** `noof_ledger_demo` stays between runs and is refreshed with
   fixed mock data on each run. `.\run.ps1 demo` refreshes it and starts the real app on it.
2. **Screenshots from that database, committed to the repo.** `.\run.ps1 screenshots` refreshes it,
   starts the app, photographs every page at phone and desktop size and the bot's replies as
   Telegram-style pictures, and writes them to `docs/screenshots/`. The README shows the key ones.
   Unchanged screens produce identical files, so git and GitHub's image diff show exactly what a
   change did to the look.

## Decisions (operator's, 2026-09-26)

| # | Decision |
|---|---|
| S-1 | Two parts: a long-lived demo database refreshed with mock data on every run, and screenshots taken from it. |
| S-2 | Mocks are for pictures, not behaviour: fake keys and a fake backup record so the app looks set up; bot replies drawn from the app's real reply text. No working fake Telegram, no fake model. |
| S-3 | Screenshots are committed under `docs/screenshots/` and shown in `README.md`; git history is the before/after. |
| S-4 | A change to anything the operator sees updates the mock data if needed, regenerates the screenshots, commits the changed images with the change, and sends them to the operator. |

Dropped from the first draft — `docs/BACKLOG.md` records them so they are not re-proposed as new: a
live fake Telegram chat driving the real bot, a rule-based fake model, seeding by replaying a
conversation through the app, labelled before/after screenshot runs.

## 1. The demo database

**`.\run.ps1 demo`** refreshes the database, then runs the real host on it in the foreground until
Ctrl+C — `dotnet run --project src\Noof.Ledger.Host -c Release --no-launch-profile`, exactly as
`run.ps1 start` does, with these environment variables (existing settings only; no new configuration
key):

| Variable | Value |
|---|---|
| `ConnectionStrings__Ledger` | the demo connection string (below) |
| `Urls` | `http://127.0.0.1:5264` — the real app keeps `5263`, so both run at once |
| `Logging__File__Directory` | `%LOCALAPPDATA%\NoofLedger\demo\logs` |
| `DataProtection__KeyRingDirectory` | `%LOCALAPPDATA%\NoofLedger\demo\dp-keys` — the real key ring is never touched |
| `Backup__Enabled` | `false` |

The password inside the connection string therefore travels only through the child process's
environment, never its arguments. Sign in as `demo` / `demo` — a password guarding mock data on a
loopback-only site, documented in `ops/RUNBOOK.md`, not a secret.

**`.\run.ps1 demo refresh`** only refreshes the database.

**Refreshing** (`tools/Noof.Ledger.Demo`, a small console app; `run.ps1` calls it):

1. Refuses if `127.0.0.1:5264` is in use — a demo is running, and dropping its database under it
   helps nobody. This also keeps two worktrees from refreshing the one demo at the same time.
   `run.ps1` makes this check itself before building anything, so a running demo's loaded DLLs never
   meet a rebuild.
2. Builds the connection string from `%LOCALAPPDATA%\NoofLedger\db.connection` (`Database=postgres`),
   rewrites it to `noof_ledger_demo`, and refuses unless the result names exactly `noof_ledger_demo`
   (`NpgsqlConnectionStringBuilder`, not a substring) — the `update-test-template` pattern. It never
   uses `LedgerConnectionString.Resolve`, whose fallback is `noof_ledger`.
3. `DROP DATABASE IF EXISTS noof_ledger_demo WITH (FORCE)`, `CREATE DATABASE`, `CREATE EXTENSION`
   `pg_trgm` and `unaccent` (migrations do not create them), then `Database.MigrateAsync()` — so the
   demo always has the schema of the branch that refreshed it. Never `EnsureCreated`.
4. Writes the mock data (below) through `LedgerDbContext`, the user through the app's password
   hasher, and the fake keys through the app's `ISecretStore` with Data Protection configured by the
   host's own `DataProtectionSetup` on the demo key ring.

`ops/clean-test-databases.ps1` adds `noof_ledger_demo` to `$Protected`. No test ever uses it.

**Mock data** (`MockData.cs`, fixed values — the same every run, so screenshots are stable):

- Dated in the current month (fixed days 1–20), with fixed ids. *(Changed during implementation from a
  fixed September 2026 window: the dashboard's "This month" follows the real calendar, and fixed dates
  would have emptied its charts from October on.)*
- Wallets: Wise (EUR), Raiffeisen (RSD, taking the RSD default from the migration-seeded Main Wallet),
  Cash (USD), Tinkoff (RUB), Kaspi (KZT), each with an opening balance; Old Revolut (EUR), archived.
  Main Wallet stays at zero — as on a fresh install.
- A few weeks of expenses with line items whose categories match what was bought, some with
  merchants; salary and freelance income; a balance check that matches and one that re-anchors.
- One completed transaction with a full trace (stages, a correction revision) and one failed
  transaction, with log rows at every level for the Logs page.
- No transaction waiting for the model: with fake AI keys the worker would call Anthropic with them.
- Secrets: `anthropic-api-key` and `groq-api-key` set to `demo-not-a-real-key`; one successful
  `backup_runs` row. That row is the one thing dated at refresh time, not in the fixed window, because
  the Backups check judges a backup's age against now; the time it displays is hidden in the
  pictures. The Telegram token stays empty — a fake token would make the app call Telegram
  every 5 seconds for as long as the demo runs — so Diagnostics shows Telegram as not configured.

**Standing rule:** a feature that adds something visible adds mock data for it here.

## 2. Screenshots

**`.\run.ps1 screenshots`** runs `tools\Noof.Ledger.Demo shots`: refresh the database, start the
host as `demo` does (a child process, stopped afterwards), take the pictures with Playwright for .NET
(`Microsoft.Playwright` 1.62.0, matching the E2E suite; the same Chromium install), write them.

**App pages** — phone 390×844 at device scale 2, and desktop 1440×900; full page; animations off,
caret hidden. Per page: navigate → ready marker → network idle → 500 ms settle (these
`InteractiveServer`, `prerender:false` pages show a pre-circuit shell until then). Markers are the
ids the E2E tests already wait on:

| Screen | URL | Ready marker |
|---|---|---|
| `login` | `/` signed out | `input[name='username']` |
| `dashboard` | `/` | `#balances` |
| `wallets` | `/wallets` | `#create-wallet` |
| `transactions` | `/transactions` | `#transactions-grid` |
| `trace` | `/transactions/<completed id>/trace` | `#trace-timeline` |
| `trace-failed` | `/transactions/<failed id>/trace` | `#trace-timeline` |
| `diagnostics` | `/diagnostics` | `#diagnostics-checks` |
| `logs` | `/diagnostics/logs`, filtered to the mock window | `#logs-grid` |
| `log-settings` | `/diagnostics/logs/settings` | `#retention-verbose` |
| `secrets` | `/settings/secrets` | `#status-anthropic-api-key` |

**Stable output.** Anything that shows the real current time is pinned before the shot, so an
unchanged screen gives a byte-identical file: the Logs page is filtered to the mock data's dates (the
host's own startup rows fall outside them), the new-wallet form's date is set to a fixed date, and
any remaining live value (a check's duration, a "checked at" time) is hidden with Playwright's
screenshot `Style`. The plan's last task proves it: two runs in a row leave `git status` clean.

**Telegram pictures** — not a working chat. A small HTML page styled after Telegram (our own CSS;
nothing copied from Telegram's GPL-licensed clients): the operator's message on the right, the bot's
reply on the left with an "edited" mark and its buttons underneath. The reply text is the app's
own — `IRecordEcho` (resolved from `AddNoofApplication`) fed with mock records, the button labels from
`RecordActionButtons`, and `/health` from `HealthReplyFormatter` — so when the bot's wording changes,
the picture changes. Phone width, device scale 2. One picture per case:

| Picture | Shows |
|---|---|
| `expense` | `coffee 350 rsd` → the recorded echo with Cancel / Edit |
| `receipt` | a multi-line message with a merchant → the echo listing each line |
| `income` | `salary 2800 eur` → the income echo |
| `balance` | `wise balance 3050` → the balance statement echo (matches / adjusted) |
| `cancel-restore` | a cancelled echo with Restore |
| `correction` | Edit → "What should I fix?" → `no, 3900` → the corrected echo |
| `failure` | an unreadable message → the failure echo with Edit |
| `health` | `/health` → the health reply |

**Output** (committed):

```
docs/screenshots/
  README.md                  generated gallery of every image, in screen order
  app/<screen>-phone.png
  app/<screen>-desktop.png
  telegram/<picture>.png
```

The tool compares each new image with the file already there, rewrites only those that differ, and
prints the changed ones. For sending them to the operator's phone it also writes JPEG copies of the
changed images, cut into slices no taller than 4000 pixels (a 780×13528 PNG was refused by the file
transfer; 4000-pixel slices went through), into `artifacts/screenshots/` — git-ignored already.

**README.md** gains a "Screenshots" section: the dashboard (desktop and phone), the transactions page,
a trace, and two Telegram pictures, linking to `docs/screenshots/README.md` for the rest.

## 3. The rule

One bullet in `CLAUDE.md` §5 Conventions, which every session reads:

> **Screenshots stay current** *(settled 2026-09-26)*. A change to anything the operator sees — a
> page, or a message the bot sends — adds mock data for anything new (`tools/Noof.Ledger.Demo`), runs
> `.\run.ps1 screenshots`, commits the changed images in `docs/screenshots/` with the change, and sends
> them to the operator (the command lists them; phone-sized copies are in `artifacts/screenshots/`).
> A new page or bot reply gets a screen or picture added. Never from `noof_ledger`.

`docs/CLOSING-A-PHASE.md` gains one line: the screenshots are current and committed.
`ops/RUNBOOK.md` gains a "Demo and screenshots" section.

## 4. Code layout and tests

- `tools/Noof.Ledger.Demo` — console app referencing `Noof.Ledger.Host` (and through it the rest) and
  `Microsoft.Playwright`; listed in `NoofLedger.slnx` under a new `/tools/` folder, so a change that
  breaks the mock data fails the build. It needs `InternalsVisibleTo` from `Noof.Ledger.Persistence`
  (`LedgerDbContext`), `Noof.Ledger.Host` (`DataProtectionSetup`, `PasswordHasherAdapter`) and
  `Noof.Ledger.Telegram` (`HealthReplyFormatter`, `RecordActionButtons`) — its only production-side
  change, and not a behavioural one.
- `tests/Noof.Ledger.Demo.Tests`, TDD as everywhere; nothing in it touches `noof_ledger_demo`:

| Subject | Kind |
|---|---|
| Connection guard: only exactly `noof_ledger_demo`; a failed rewrite refuses | unit |
| Telegram page: a reply's text, "edited" mark and button labels appear in the generated HTML | unit |
| Gallery `README.md` lists every screen and picture | unit |
| Mock data writes cleanly into a clone of `noof_ledger_test_template`, and the dashboard's balance read model returns the expected balance for each wallet | database (runs filtered, like other DB classes) |
| No `src` project references anything under `tools/`; `Noof.Ledger.Demo` is the only non-test `InternalsVisibleTo` target in `src` | architecture |

## Out of scope

A working fake Telegram or fake model; voice notes and receipt photos in the pictures (receipts
after Phase 6 lands — the rule then adds them); pixel-diff regression testing; screenshots of
`noof_ledger`.
