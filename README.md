# noof-ledger

A personal finance tracker, built for one person and one machine.

Capture spending through a Telegram bot — typed, spoken, or photographed — let an LLM categorise
each line item, and see where the money went on a local Blazor dashboard that understands multiple
wallets and currencies.

> **Status: the capture path works end to end.** A message typed to the Telegram bot becomes a
> categorised expense on the dashboard. Say it the way you would say it — *"купил вчера штуку
> евро"* is recorded as 1000 EUR dated yesterday, echoed back in the chat, and can be cancelled
> with one tap or corrected by a reply. Voice notes work the same way: say it, and the bot records
> what it heard.
> 675 tests, all green — browser tests included.
>
> Still missing before it can carry a year of real spending: **balances** (there is no arithmetic
> over wallets yet), **receipt photos**, **currency exchange**, and — the one that matters most —
> **a backup that has actually been restored at least once**. An untested backup is a hypothesis.

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
