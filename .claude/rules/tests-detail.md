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
- **Test databases are created with `STRATEGY FILE_COPY`, and all admin DDL goes through
  `Noof.Ledger.TestKit.DatabaseSettings`** *(Phase 6)*. Every `DROP DATABASE` forces a checkpoint and
  waits for it. Under PostgreSQL's default `WAL_LOG` strategy a clone's ~300 files go through shared
  buffers, so that checkpoint must fsync every clone created since the last one and still alive —
  measured with 40 live clones, one `CHECKPOINT` took 42.6s under `WAL_LOG` and 0.17s under
  `FILE_COPY`. That, not the timeout values and not only a second checkout, is what made full-suite
  drops outlast 120s; raising timeouts never fixed it. `DatabaseSettingsCloneStrategyTests` guards the
  strategy. Every fixture uses `DatabaseSettings.OpenAdminConnectionAsync`/
  `CreateDatabaseFromTemplateAsync`/`CreateEmptyDatabaseAsync`/`DropDatabaseAsync` for one statement,
  or `OpenAdminConnectionAsync` + `AdminCommandTimeoutSeconds` for a batched loop
  (`PostgresFixture.DisposeAsync`) — never its own copy of the `CREATE`/`DROP` boilerplate. Full
  database/E2E runs still go one at a time across checkouts (CLAUDE.md's end-of-phase rule).
- **Persistence tests run in parallel with each other, inside the one `"postgres"` collection**
  (`parallelMode: all`, `maxParallelThreads: 12`, 2026-09-28): a full run went 355 s → 127 s, peak
  ~20 server connections. `maxParallelThreads` alone did nothing — xUnit never parallelises within a
  collection in `collections` mode, and every database test shares that one. So a test owns only its
  own clone: one that touches anything process- or server-wide (environment variables, the template
  itself, server settings, a machine-wide process count) goes in a `DisableParallelization`
  collection — `ProcessEnvironmentCollection`, or `"postgres-serial"` when it needs clones — and
  every clone path awaits the template freshness guard first.
