# noof-ledger

A personal finance tracker, built for one person and one machine.

Capture spending through a Telegram bot — typed, spoken, or photographed — let an LLM categorise
each line item, and see where the money went on a local Blazor dashboard that understands multiple
wallets and currencies.

> **Status: in development.** The foundation and the authentication layer are built and tested.
> Telegram capture, LLM categorisation and the dashboard itself are not written yet. Nothing here
> is ready to track real money.

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

Off by default, and honest about why. The app binds `127.0.0.1` on a machine that is only on while
its owner is logged in. Against that owner, a login screen buys nothing — that principal already
holds the database credentials.

`Auth:Mode` (`Off` | `Cookie`) selects **which handler is registered**; it never branches the
middleware pipeline. `[Authorize]` ships on every page from the first commit, so a forgotten flag
cannot leave a route ungated.

The interlock that makes this safe: **if Kestrel binds anything but loopback while auth is off, the
host logs the reason and exits.** The day you widen the binding to reach the dashboard from a
phone, the app refuses to start until you turn auth on.

```
Ledger.Host.exe user set-password <name>
```

is the only way a user is ever created. There is no registration page — a password in any settings
file is one commit from being permanent in a public repository.

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

Not yet chosen. Until one is added, no permission is granted to use this code.
