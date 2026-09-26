---
paths:
  - "tests/**"
---

## CLAUDE.md §4 — Testing details

- **Test hosts never write into the operator's real log directory** (Phase 5).
  Every `WebApplicationFactory<Program>` and E2E host fixture must point `Logging:File:Directory` at a
  per-fixture temp directory (the `TestHostLogging` helpers, guarded by `TestHostLogDirectoryTests`) —
  before this, test runs wrote files straight into `%LOCALAPPDATA%\NoofLedger\logs` and could evict the
  operator's own logs under the 14-file retention cap. `Host.Tests` runs with
  `parallelizeTestCollections=false` because Serilog's logger is a shared static.
- **No production configuration key exists solely so a test can flip a code path** *(settled
  2026-09-25, Phase 5)*. A `Diagnostics:ForceLogSinkFailureForTests` hook was added, then removed in
  review, in favour of driving the real failure (an unreachable sink) from the test itself.
- **Never seed a test with `DateTimeOffset.UtcNow` and then assert exact equality against a value
  read back from PostgreSQL.** `timestamptz` keeps microseconds; a .NET tick is 100ns. A timestamp
  whose final tick digit is non-zero is truncated on the round trip, so the assertion fails most
  runs but not all — the worst kind of flake. Seed from a fixed literal, or compare with
  `BeCloseTo`. This shipped twice before it was caught.
- **Admin DDL in test fixtures (`CREATE`/`DROP DATABASE`, and the admin connection itself) always uses
  the explicit connect/command timeouts on `Noof.Ledger.TestKit.DatabaseSettings`, never Npgsql's 30s
  default** *(Phase 6)*. Every fixture that clones the test template goes through
  `DatabaseSettings.OpenAdminConnectionAsync`/`CreateDatabaseFromTemplateAsync`/`DropDatabaseAsync`
  rather than its own copy of the `CREATE`/`DROP` boilerplate. Set these because CREATE/DROP DATABASE
  is uniquely slow to wait on — but the Phase 6 full-suite timeouts this was written for (11 failures,
  even at 120s) turned out to be caused by a second checkout running its own full suite against the
  same PostgreSQL server at the same time, not by an under-timed admin connection; raising the timeout
  alone did not fix them, and never will for that cause. **Full database/E2E runs go one at a time
  across checkouts** — CLAUDE.md's "Database and E2E test projects run filtered... and in full once at
  the end of a phase" rule exists for exactly this. Keep these explicit timeouts regardless: they are
  still the right defence against genuine checkpoint/connect slowness under a loaded shared server,
  just not what closed this particular flake.
