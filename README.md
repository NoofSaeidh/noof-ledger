# noof-ledger

A personal finance tracker, built for one person and one machine.

Capture spending through a Telegram bot — typed, spoken, or photographed — let an LLM categorise
each line item, and see where the money went on a local Blazor dashboard that understands multiple
wallets and currencies.

> **Status: capture, balances, backup and observability all work end to end.** A message typed to the
> Telegram bot becomes a categorised expense, income, or balance statement on the dashboard — say it
> the way you would say it, and the model reads the amount, the date, the kind and which wallet from
> how you speak it. Every wallet's balance — opening balance, minus spending, plus income, corrected
> by your own balance statements — is exact in all five supported currencies (EUR, RSD, USD, RUB,
> KZT), proven under both `ru-RU` and `sr-Latn-RS`. Wallets are created and managed on a `/wallets`
> page; income and balance statements are ordinary messages to the bot, typed or spoken. The app
> backs its own database up daily, and `ops/restore-check.ps1` restores a dump into a scratch
> database and compares every wallet's balance and row count against the source — proven so far
> against a template clone; the one restore check against the live ledger itself is the operator's
> to run (see `ops/RUNBOOK.md`).
>
> The app now watches itself. Stop PostgreSQL and the host keeps running — the sign-in page and every
> other page show "Waiting for the database…" instead of crashing or erroring, and it comes back up
> by itself the moment PostgreSQL does. Every log line goes to a rolling file and, while the database
> is reachable, to an `app_log` table too, with every known secret redacted before either sink sees
> it. A message's whole journey — received, transcribed, categorised, persisted, replied — and its
> later edits are on one trace page. System health (database, migrations, Telegram, the AI keys,
> backups, disk space, the log sink itself) shows as a tile on the dashboard, in full on
> `/diagnostics`, and to the operator alone via `/health` in the bot.
>
> 1054 tests — 1043 passing, 11 skipped (they call a live model or a live voice provider and need
> keys), none failing. Browser tests included.
>
> Still missing: **receipt photos** and **currency exchange** (a spend in a currency other than its
> wallet's own is recorded as-is, in its own currency, not converted).

## Why it looks the way it does

**Money is `decimal` + `Currency`, stored as PostgreSQL `numeric(19,4)`.** Never `double`, never a
scaled integer, never a string. That choice was made by experiment rather than preference: SQLite
stores `decimal` with text affinity, which under `ru-RU` throws on read and under `sr-Latn-RS`
sorts `1234.50` below `999.99` — a wrong answer with no warning. The counterfactual is reproduced
in this repository's history.

**`Money` refuses to compare across currencies.** Ordering 10 EUR against 10 USD has no correct
answer, so `CompareTo` throws exactly as `+` does. Sorting a mixed list means saying how:
`OrderBy(m => m.Currency).ThenBy(m => m.Amount)`.

**`CurrencyCode` sorts ordinally, never by culture.** In Serbian Latin, `LJ` is a single collating
letter — culture-sensitive comparison silently reorders currency codes.

**Boundaries are enforced by tests, not by convention.** Project references are transitive at
compile time, so a test parses each `.csproj` and asserts its exact reference set. `Ledger.Web` is
UI only: no `DbContext`, no EF types, no `HttpClient`, no `Program.cs`.

## Authentication

Always on. Cookie authentication is the only mode — a config toggle that can turn login off is one
more thing that can be left in the wrong state, and simpler beats configurable here.

Every routable page declares its authorization, and a test enumerates the pages to prove it: one
that says nothing is a failure, and the sign-in page is the only one allowed to be anonymous. That
rule exists because the convention had already drifted — a leftover template page was reachable
without signing in, and nothing said so.

Endpoints get the same guarantee from the other direction: the authorization fallback policy denies
anonymous callers, so an endpoint that names no policy is closed rather than open. Only three things
opt out, each deliberately — the sign-in page, the form it posts to, and `/healthz`. Static assets
opt out too, or the sign-in page would arrive without the stylesheet and script it needs.

The interlock that makes this safe regardless: **if Kestrel binds anything but loopback, the host
logs the reason and exits — unconditionally, not only while auth is off.** The day you widen the
binding to reach the dashboard from a phone, the app still refuses to start; loopback-only is
enforced no matter what.

```
Ledger.Host.exe user set-password <name>
```

is the only way a user is ever created. There is no registration page — a password in any settings
file is one commit from being permanent in a public repository.

## Observability

Logs live under `%LOCALAPPDATA%\NoofLedger\logs` by default — a daily rolling file, 14 kept, 50 MB
cap each — configurable through `Logging:File:Directory` in `appsettings.json`. While PostgreSQL is
reachable the same events also go into the `app_log` table, with every known secret redacted before
either sink sees a line; if the database is down or the table sink itself is failing, the file is
still the durable copy.

`/diagnostics` lists every health check (database, pending migrations, Telegram, the AI keys, the
daily backup, disk space, the log sink) with a link into `/diagnostics/logs` — a paged, filterable
view over `app_log`, falling back to a tail of the log file when the database sink is unavailable.
Every Telegram message that becomes a transaction gets a trace page at `/transactions/{id}/trace`:
its path from received to replied, with timings, and its full edit history. The dashboard carries a
one-line health tile with a link to `/diagnostics`; the operator alone can also ask the bot directly
by sending `/health`.

If PostgreSQL is stopped, the host does not exit — the sign-in page and every other page show a
"Waiting for the database…" banner, the log file records every retry, and the app resumes on its own
the moment PostgreSQL is reachable again. See `ops/RUNBOOK.md` for the manual check.

## The interface

MudBlazor, in a dark theme defined in one C# file. The component library is not cosmetics: a
dashboard that needs a card, a chart and a table is assembly rather than invention, and Phase 2
onward needs several more of them.

The app never fetches a typeface from the internet — it binds to loopback, holds financial data and
should work with the network down, none of which survives a stylesheet pulled from Google on every
page load. The theme names the machine's own fonts instead.

Render modes are per-page and stay that way. The sign-in page is a real HTML form POST, because
`SignInAsync` needs an `HttpContext` that an interactive circuit does not have. That single fact
decides the rest: `MainLayout` renders statically, MudBlazor's popover, dialog and snackbar
providers cannot work from there, and so nothing in the app uses a popover, dialog, snackbar,
tooltip or menu. Feedback is an inline alert. An inert provider is worse than an absent one.

## Running it

Requires .NET 10 SDK and PostgreSQL 18.

```bash
ops/reset-database-auth.ps1        # one-time: creates databases, writes a credential outside the repo
dotnet test --solution NoofLedger.slnx
ops/publish.ps1                    # builds, tests, and publishes to publish/
```

`ops/publish.ps1` refuses to publish if the suite did not actually run — an exit code alone once
let a zero-test run look like a pass.

Tests run against a real PostgreSQL database, never an in-memory provider. The offline suite proves
the DDL is right; only a real database proves precision, collation and constraints behave.

## Layout

```
Domain  ←  Application  ←  Persistence · Ai · Fx · Receipts · Telegram · Web  ←  Host
```

`Domain` has zero NuGet dependencies, asserted by a test.

## Licence

None, deliberately. This is published to be read, not reused — default copyright applies, so no
permission is granted to copy, modify or distribute it.
