---
title: "`run.ps1 test db -Filter` filters only the Persistence tests"
status: deferred
area: ops
since: 2026-10-08 (Phase 8a close)
related: [loose-ends-phase-5-observability]
---
**What.** `.\run.ps1 test db` runs three projects under the suite lock: `Noof.Ledger.Persistence.Tests`, then the
`Category=Database` classes of `Noof.Ledger.Host.Tests` and of `Noof.Ledger.Demo.Tests`. `-Filter` reaches only the
first. Host and Demo always run their whole Database-tagged set, and each project runs through `Invoke-Checked`, so
the first failing project stops the rest.

**Why it matters.** The project's testing rule is to run database classes filtered to what a change touches, but a
Host or Demo class cannot be targeted through the script: `-Filter BugsCommandDbTests` makes Persistence match no
test, and Microsoft.Testing.Platform exits 8 ("zero tests ran"), which stops the run before Host is reached at all.
Without a filter, Host's `DatabaseLogLevelDbTests` failure (`loose-ends-phase-5-observability.md`) stops it before
Demo. Phase 8a's stages therefore ran their Host and Demo database classes with `dotnet test` directly under the
suite lock, which is easy to get wrong (the lock is the script's).

**What fixing it would take.** In the `db` branch, pass `-Filter` to all three projects — combined with
`Category=Database` for Host and Demo, as `fast` already combines its exclusion — and let a project with no
matching test pass (`--ignore-exit-code 8`), failing the command only when none of the three ran a test. A
`RunScriptTests` fact pins it.
