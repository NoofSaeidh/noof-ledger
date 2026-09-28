---
title: The test template can silently drift from what migrations produce — fixed for Persistence 2026-09-28; shared template still unguarded
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

**Persistence.Tests: fixed 2026-09-28.** It no longer clones the shared template. The
`MigratedTemplate` assembly fixture migrates a private `noof_test_tpl_*` database from empty once per
run and every clone copies that, so it cannot lag the migrations. `MigratedTemplateTests` compares a
clone's migration history, columns, constraints, indexes, triggers, views and functions with a
freshly migrated database. Run against the shared template the day it was written, that test failed:
the shared template carried the `pg_trgm` and `unaccent` extensions, which no migration creates — the
migration-id guard it replaced had passed all along.

**Still open: the E2E suite and the `Category=Database` Host.Tests classes** clone the shared
template, and nothing checks it. The follow-up is the same move for them: build one migrated
`noof_test_tpl_*`-style template per test run (or per fixture) through a TestKit helper, clone that,
and retire `update-test-template` and the shared template once no suite names it. Cost: one full
migration per run for each of those suites.
