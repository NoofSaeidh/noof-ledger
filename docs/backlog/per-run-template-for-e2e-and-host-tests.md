---
title: E2E and database-tagged Host tests still clone the shared test template
status: deferred
area: tests
since: 2026-09-28
related: [test-template-can-silently-drift-fixed]
---
**Wanted.** Give the E2E suite and the `Category=Database` classes in `Noof.Ledger.Host.Tests` the
same per-run template that `Noof.Ledger.Persistence.Tests` got on 2026-09-28: migrate an empty
database once per test run (from `template0`), clone it for each test, drop it at the end.

**Why.** Those suites still clone the long-lived `noof_ledger_test_template`, which has to be
brought up to date by hand with `.\run.ps1 update-test-template` after every migration, can drift
from what the migrations produce (an applied migration edited in place keeps its id; this has
happened — see the related entry), and already differs from a fresh migration: it carries the
`pg_trgm` and `unaccent` extensions that `ops/reset-database-auth.ps1` installs and no migration
creates. A test that passes on it proves less about a fresh install than it appears to.

**Why it is not done yet.** The Persistence change was scoped to one test project to keep the PR
reviewable. The E2E fixtures start real hosts against the clone, so the per-run template has to be
created before the first host starts and outlive every host of the run — a different lifetime
from the Persistence collection fixture, worth its own design.

**Done when.** No test project clones `noof_ledger_test_template`; `update-test-template` is
either deleted or kept only for the demo/tooling paths that genuinely need a persistent database,
and the rules in `.claude/rules/database.md` say so.
