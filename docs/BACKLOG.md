# Backlog — future improvements

Work that is wanted but deliberately not scheduled. Distinct from `OPEN-QUESTIONS.md`, which holds
deferred *decisions*; this holds deferred *work* whose decision is already made.

Nothing here blocks any phase. An item leaves this file only by being written into a phase plan.

---

## Category management screen

**Wanted.** A page to add, rename, re-parent and deactivate categories, including sub-categories.

**Why it is not in Phase 1.** The schema carries the whole cost of this feature and is built in Phase 1's
first migration: `Guid` keys, a self-referencing `ParentId`, bilingual `NameEn`/`NameRu`, and — the part
that actually makes renaming safe — an immutable `Slug` that the model answers with, so a display name
can be rewritten without invalidating a single stored categorisation. Retrofitting any of that would mean
a migration plus re-categorising history through the LLM. A CRUD page on top of a schema that already
supports the operations is ordinary work that can land in any later phase at the same cost.

**Until then.** The seeded tree is what exists. Renaming is possible at the data level from the first
migration; there is simply no UI for it.

**Sequencing note.** Worth doing together with per-line-item correction below — both are small edit
surfaces over data that already supports them, and they share the same page furniture.

---

## Per-line-item category correction

**Wanted.** "No, that one was actually Transport" on a single line item.

**Why it is not scheduled.** The only recategorisation UI the spec names is `/recategorize`, which is
Phase 7 and is a bulk, merchant-rule-driven operation — not a one-row fix. That leaves every phase
between 1 and 7 with no way to correct a single miscategorised item.

**Cost already paid.** `CategorizationAuthority.User = 4` ships in Phase 1's enum and its integer value
is pinned by a test, so the precedence guard already knows a human outranks the model. The retrofit is
a page, not a migration.

---

## Revalidating authentication state for long-lived circuits

**Wanted.** A circuit that stops serving a user whose cookie was revoked or expired.

**Why it matters.** Blazor Server holds an authenticated `ClaimsPrincipal` for the life of the SignalR
circuit. `AddCascadingAuthenticationState` alone does not revalidate it, and there is no custom
`AuthenticationStateProvider` anywhere in this repository. The combination that makes this concrete is
`[Authorize]` plus `@rendermode InteractiveServer` — which is exactly the settings page that handles
secrets.

**Status.** Flagged by the Phase 1 readiness audit and never investigated. Belongs with the hardening
phase recorded in `OPEN-QUESTIONS.md`, or earlier if `Auth:Mode=Cookie` is ever switched on.

---

## Multi-item capture from one message

**Wanted.** `кофе 250 рсд, такси 500 рсд` in a single Telegram message becoming two line items.

**Why it is not in Phase 1.** An explicit non-goal, recorded so it is a decision rather than an
oversight. The multi-`LineItem` design is framed around Phase 4 receipts, and `Transaction` already
holds a collection of them, so this is a change to the extraction contract and the worker — not to the
schema.

---

## The test suite's per-test database strategy will not scale much further

**Symptom, observed twice in one phase.** `PostgresFixture` creates a fresh PostgreSQL database per
test and drops them all at collection teardown. During Phase 1A task 6 the Persistence collection
crossed roughly fifty such databases per run and teardown began hitting Npgsql's 30-second command
timeout. A live `pg_stat_activity` check showed the stuck `DROP DATABASE` blocked on
`wait_event = CheckpointDone` — a genuine PostgreSQL checkpoint wait under churn, not a leaked
connection. Separately, a full-solution run immediately after the Playwright suite failed 34
Persistence tests on connection timeouts and passed cleanly on retry. **279 orphaned `noof_test_*`
databases** had accumulated from the hung runs and were dropped by hand.

**What was done.** The teardown `DROP DATABASE` commands got `CommandTimeout = 120`. That treats the
symptom and was the right call mid-task. It is not a fix.

**Why it matters.** Phase 1A alone roughly doubled the Persistence test count, and Phase 1B adds the
worker, the extraction contract and the merchant path. A suite that intermittently fails 34 tests and
needs a retry is one that stops being trusted, and an untrusted suite stops being run.

**The shape of a real fix.** One database per *collection* rather than per test, with each test wrapped
in a transaction that is rolled back — the standard answer, and it removes the churn entirely. The
obstacle is that several tests call `MigrateAsync` themselves and some assert on schema objects, so
they cannot all share one database unchanged. Worth doing before the Persistence suite grows again,
and worth measuring first: the win is wall-clock as much as reliability.

**Do not** reach for the EF InMemory provider. `CLAUDE.md` bans it for good reasons, and every one of
these tests exists precisely because it runs against real PostgreSQL.
