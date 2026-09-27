---
title: The test template can silently drift from what migrations produce — fixed 2026-09-22
status: deferred
area: persistence
since: 2026-09-22
---
`noof_ledger_test_template` is a long-lived database that tests clone. It is only ever migrated
forward, so it carries whatever schema it had when each migration was first applied to it — and an
*edit* to an already-applied migration never reaches it. That is exactly what happened with
`merchant_aliases_no_truncate`: the template, and `noof_ledger` alongside it, spent a phase without
the TRUNCATE guard while every freshly created database had it. Found by accident, because switching
`CreateContextAsync` to clone the template made
`MerchantAliasWriteOnceTests.Truncating_the_alias_table_is_rejected_by_the_database` fail — a true
positive from an experiment that was measuring something else entirely.

Both databases were dropped and recreated from migrations, and `.claude/rules/database.md` now
carries the rule that caused it. What is still missing is a guard: nothing compares the template
against a freshly migrated database, so the next drift will be found the same way — by luck. A test that migrates a
scratch database and diffs `pg_dump --schema-only` against the template would close it, at the cost
of one full migration run per suite execution. Phase 2 added the runbook step "After adding a
migration" and a Database rule; the drift guard itself is still missing.
