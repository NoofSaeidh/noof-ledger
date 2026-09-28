---
title: The Persistence test suite takes 1m48s and nobody has found where it goes
status: done
area: tests
since: 2026-09-22
---
**Resolved 2026-09-28.** The missing time was serial execution. Every database test class sat in
one xUnit collection (`"postgres"`), and xUnit never runs two tests of one collection at the same
time — which is why `maxParallelThreads` 4 and 8 measured the same below: the setting had nothing
to parallelise. `"parallelMode": "all"` with `maxParallelThreads: 12` let the collection's tests
run concurrently (a machine-wide `pg_dump` test moved to its own `postgres-serial` collection),
and the full run fell from 355 s to 127 s with no failures or connection errors across repeated
runs (PR #21). At 12 threads, with per-test connections opened with `Pooling=false`, the earlier
socket failures (seen at 16 threads) did not appear; 16 was not re-tested. Kept as `done` because
the ruled-out causes below are still worth not re-investigating.

Measured 2026-09-22, deferred by the operator. Two plausible causes were tested and **both ruled
out by measurement**, so the next person should not start from either:

- **Not test concurrency.** `maxParallelThreads` at 4 and at 8 finish in the same time (1:48.5 vs
  1:48.0). It is capped at 4 in `xunit.runner.json` because 16 — the default, one per CPU thread —
  produced transient socket failures that failed the publish gate two runs in three, not because 4
  is faster.
- **Not replaying migrations.** `CreateContextAsync` creates an *empty* database and lets each test
  run the whole migration chain, so cloning the already-migrated template instead looked like an
  obvious win. It is not: 1:51 against 1:48, i.e. nothing. (It did expose a real defect — see the
  entry about the template's missing trigger — so the experiment paid for itself anyway.)

What is known: `CREATE DATABASE ... TEMPLATE` costs **240ms** measured serially, `DROP` about
**50ms**, and there were 135 tests then. That accounts for roughly 32 of the 108 seconds. **The other 76
seconds are unaccounted for.** The next step is to instrument one test end to end — database
creation, migration, EF model build, the test body, teardown — rather than guessing again.

A classification of all 135 tests was done for a shared-database refactor: **74 could share** a
database (they insert their own rows under fresh GUIDs and assert only on those), **61 genuinely
cannot**. The blockers are architectural rather than sloppy: `EfJobQueue.ClaimAsync` scans the whole
table with `FOR UPDATE SKIP LOCKED` and depends on being the only writer; `EfSpendingReadModel`
aggregates across every row by design; `EfSecretStoreTests`, `EfUserStoreTests` and
`MerchantAliasWriteOnceTests` reuse fixed natural keys (`SecretKeys.AnthropicApiKey`, `"noof"`,
`"TEST MERCHANT"`) that would collide; and `SeedDataTests` renames the seeded coffee category its own
sibling asserts on. So sharing buys a ~55% cut in database creations, not the ~95% the idea
suggests — worth perhaps 18 of those 32 seconds, against a real risk of turning deterministic
failures into timing-dependent ones.
