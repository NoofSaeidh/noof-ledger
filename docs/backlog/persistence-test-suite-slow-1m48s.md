---
title: The Persistence test suite takes 1m48s and nobody has found where it goes
status: deferred
area: tests
since: 2026-09-22
---
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
**50ms**, and there are 135 tests. That accounts for roughly 32 of the 108 seconds. **The other 76
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
