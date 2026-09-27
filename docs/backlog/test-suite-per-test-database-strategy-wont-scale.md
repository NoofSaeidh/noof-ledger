---
title: The test suite's per-test database strategy will not scale much further
status: deferred
area: tests
---
**Symptom, observed three times now.** `PostgresFixture` creates a fresh PostgreSQL database per
test and drops them all at collection teardown. During Phase 1A task 6 the Persistence collection
crossed roughly fifty such databases per run and teardown began hitting Npgsql's 30-second command
timeout. A live `pg_stat_activity` check showed the stuck `DROP DATABASE` blocked on
`wait_event = CheckpointDone` — a genuine PostgreSQL checkpoint wait under churn, not a leaked
connection. Separately, a full-solution run immediately after the Playwright suite failed 34
Persistence tests on connection timeouts and passed cleanly on retry. **279 orphaned `noof_test_*`
databases** had accumulated from the hung runs and were dropped by hand.

A third variant surfaced during Phase 2: `dotnet test --solution` started failing with Npgsql
53300 `sorry, too many clients already` in `EfCaptureStoreTests` and `EfJobQueueTests`, growing
with the test count (0 failures at ~487 tests, 5 at 513, 10 at 532, 14 at 544). `pg_stat_activity`
sampled through a full run showed the connection count climbing in lockstep with distinct
`noof_test_*` databases — 10/10, 26/28, 58/61, 86/92 (db/idle-conn) — until it passed the server's
`max_connections` (100) at 102 total. Cause: every per-test database gets its own connection
string, so Npgsql keeps a separate pool per database; `PostgresFixture.DisposeAsync` only calls
`NpgsqlConnection.ClearAllPools()` once, at the very end of the whole `"postgres"` collection, so
every one of the collection's ~100+ per-test databases left an idle pooled connection open on the
server for the rest of the run.

**What was done.** The teardown `DROP DATABASE` commands got `CommandTimeout = 120` for the
checkpoint-wait variant. That treats the symptom and was the right call mid-task. It is not a fix.
(The checkpoint-wait variant's cause was found in Phase 6: `WAL_LOG` clones — see "Clone
`CREATE`/`DROP DATABASE` timeouts in full runs — closed 2026-09-27" below.)
For the pool-exhaustion variant, `PostgresFixture.CreateEmptyDatabaseConnectionStringAsync` and
`CreateDatabaseAsync` now build their connection strings with `Pooling = false`: each per-test
database is used by exactly one test, so Npgsql's pool buys nothing, and disabling it makes
`Dispose()` close the physical connection immediately instead of parking it until collection
teardown. Also a treatment, not the fix below — but confirmed by two consecutive
`dotnet test --solution` runs at 0 failures / 544 total after carrying 14 failures before.

**Why it matters.** Phase 1A alone roughly doubled the Persistence test count, and Phase 1B adds the
worker, the extraction contract and the merchant path. A suite that intermittently fails 34 tests and
needs a retry is one that stops being trusted, and an untrusted suite stops being run.

**The shape of a real fix.** One database per *collection* rather than per test, with each test wrapped
in a transaction that is rolled back — the standard answer, and it removes the churn entirely. The
obstacle is that several tests call `MigrateAsync` themselves and some assert on schema objects, so
they cannot all share one database unchanged. Worth doing before the Persistence suite grows again,
and worth measuring first: the win is wall-clock as much as reliability.

**Do not** reach for the EF InMemory provider. `.claude/rules/database.md` bans it for good reasons,
and every one of these tests exists precisely because it runs against real PostgreSQL.
