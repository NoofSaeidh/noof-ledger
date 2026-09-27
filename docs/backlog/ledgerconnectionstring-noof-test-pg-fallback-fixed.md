---
title: "`LedgerConnectionString`'s `NOOF_TEST_PG` fallback was a landmine for a locally launched publish output — fixed 2026-09-22, commit `59e4783`"
status: done
area: persistence
since: 2026-09-22
---
**Symptom, hit while closing Phase 1B.** `ops/publish.ps1`'s published `appsettings.json` ships
`ConnectionStrings:Ledger` empty by design (the operator fills it in on the real machine).
`LedgerConnectionString.Resolve` fell back to the `NOOF_TEST_PG` environment variable when that's
empty, rewriting its `Database=postgres` to `Database=noof_ledger` — the real database name. A dev
shell with `NOOF_TEST_PG` already set (ordinary local test setup, unrelated to publishing) that then
launches `publish/Noof.Ledger.Host.dll` directly connects to, and writes to, the operator's real
database with no prompt and no warning. It happened during this phase's close: the process ran a
handful of read-only dashboard queries plus repeated idempotent `ReleaseExpiredLeasesAsync` UPDATEs
against `categorization_jobs` before being killed. No row was inserted, deleted, or dropped, but the
near miss is the point.

**Why it stayed dangerous until it did not.** `NOOF_TEST_PG` existing at all is deliberate
test-suite convenience (`docs/decisions/` / `ops/reset-database-auth.ps1`), and the fallback
chain was reasonable for a test process. The unsafe case was specifically a human launching the
**published output** directly in a shell that happens to have that variable set — an operator with
a real deployment normally has `ConnectionStrings:Ledger` (or the credential file) configured and
never hits the fallback at all.

**The fix, commit `59e4783` (2026-09-22).** `LedgerConnectionString.Resolve` no longer consults
`NOOF_TEST_PG` at all — the fallback was removed outright rather than gated, since `Resolve` has no
way to tell "test project" from "published host" apart. Tests read the variable through
`Noof.Ledger.TestKit.DatabaseSettings` instead, which both end-to-end fixtures already pass
`ConnectionStrings__Ledger` explicitly through, so nothing that legitimately used the fallback lost
anything. The regression test was proved to fail first — the old fallback was put back, the test
went red, then the fallback was removed again — before being allowed to pass.
