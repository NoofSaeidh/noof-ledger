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

**Partly taken by Phase 2 (2026-09-22).** A reply to the bot's echo (*"это не еда, а подарок"*) now
corrects a record through the model. A one-click category change on a single line item in the
dashboard is still this item.

---

## Revalidating authentication state for long-lived circuits

**Wanted.** A circuit that stops serving a user whose cookie was revoked or expired.

**Why it matters.** Blazor Server holds an authenticated `ClaimsPrincipal` for the life of the SignalR
circuit. `AddCascadingAuthenticationState` alone does not revalidate it, and there is no custom
`AuthenticationStateProvider` anywhere in this repository. The combination that makes this concrete is
`[Authorize]` plus `@rendermode InteractiveServer` — which is exactly the settings page that handles
secrets.

**Status.** Flagged by the Phase 1 readiness audit and never investigated. Belongs with the hardening
phase recorded in `OPEN-QUESTIONS.md`. Cookie authentication is the only mode now
(`docs/OPEN-QUESTIONS.md`, A1/A2 SUPERSEDED), so this is no longer conditional on a mode switch — it
applies today.

---

## Multi-item capture from one message

**Wanted.** `кофе 250 рсд, такси 500 рсд` in a single Telegram message becoming two line items.

**Why it is not in Phase 1.** An explicit non-goal, recorded so it is a decision rather than an
oversight. The multi-`LineItem` design is framed around Phase 4 receipts, and `Transaction` already
holds a collection of them, so this is a change to the extraction contract and the worker — not to the
schema.

---

## The test suite's per-test database strategy will not scale much further

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

---

## `UseForwardedHeaders` is missing, and `CookieSecurePolicy.SameAsRequest` depends on it

**Wanted.** `app.UseForwardedHeaders(...)` configured before authentication, so the app sees the original
scheme when something terminates TLS in front of it.

**Why it is not scheduled.** Not reachable today: the app binds loopback only, nothing sits in front of
it, and `LoopbackGuard` refuses a non-loopback bind unconditionally now (`Auth:Mode` no longer exists;
cookie authentication is the only mode). The capture relay decided in `OPEN-QUESTIONS.md` P1-6 does not
change that — the drain is outbound, so nothing proxies inbound.

**Why it is written down anyway.** `Program.cs` sets `CookieSecurePolicy.SameAsRequest`. Behind a
TLS-terminating proxy the app sees plain HTTP and therefore ships the authentication cookie **without the
Secure flag**, silently. The day anyone puts this behind a reverse proxy — a later hosting decision, a
tunnel for phone access — that is a live session-hijack surface and nothing will warn about it.

About an hour, and it belongs with whatever first introduces a proxy.

---

## Reaching the dashboard from a phone, away from home

**Wanted.** The operator said "later, not now" when asked (2026-09-21).

**Why it is recorded.** It is the one condition that would reverse P1-6's rejection of hosting. Hosting
was declined because it costs €6/month and a full Linux port to buy only what the capture relay already
buys for nothing. If remote dashboard access becomes wanted, hosting buys two things instead of one and
the arithmetic flips — at which point the relay becomes redundant rather than complementary.

**Design consequence now:** nothing in the relay work should assume the application is unreachable from
outside. It should assume only that it is *currently* loopback-bound.

---

## A Test button for the Telegram bot token

**Wanted.** The settings page's Anthropic key gets a Test button this phase (`ISecretProbe` /
`AnthropicKeyProbe`, `GET /v1/models`, costs no tokens). The Telegram bot token has no equivalent -
the spec (§9) names `getMe` for exactly this, and today the only way to learn a pasted Telegram
token is bad is to watch the poller silently fail to start.

**Why it is not scheduled.** Out of this phase's stated scope (Task 8 covers only the Anthropic
key's Test button). `ISecretProbe` already exists as a port after this phase; a `TelegramKeyProbe`
implementing it against `getMe` is a small, isolated addition with no schema or contract cost -
ordinary work for any later phase.

---

## Merchant merge inbox for near-duplicate aliases

**Wanted.** Spec §11: "A `pg_trgm word_similarity` sweep surfaces near-duplicates in a merge inbox
- one click merges retroactively and revertibly, and rejected pairs are remembered permanently."

**Why it is not in Phase 1B.** The alias table this needs - write-once, `Fold()`-keyed, the sole
authority on merchant identity - ships this phase (`IMerchantDirectory`). The merge inbox is a
read/write UI over rows that table already produces correctly; nothing about it changes the
schema. It is explicitly Phase 7 (Governance) work in the spec's phase table, alongside the
recategorization batch UI it shares page furniture with.

**Until then.** A wrong canonicalisation on first sighting is permanent under write-once (a
named, accepted trade-off - see spec §13 risk 4), with no UI yet to correct it short of a manual
database edit.

---

# Closing the 24-hour capture gap — four researched options, none chosen

Researched 2026-09-21 across three verification passes. The first survey's prices were challenged and
**28 were found unsupported by their own citations; four were then proved wrong**. Everything below was
read off a vendor page or API on that date. Decision: **stay local, build none of it yet** — see
`OPEN-QUESTIONS.md` P1-6 SUPERSEDED.

The problem, restated: Telegram discards unfetched updates after 24 hours and a bot cannot read history
(P1-5). A PC off for a weekend loses what was sent. Pick these up when the gap is actually felt.

## 1. Wake the machine on a schedule — free, no code, try this first

Windows Task Scheduler can wake a sleeping machine to run a task. The application already drains
everything waiting whenever it starts, so **no code changes at all** — twice a day keeps every message
inside the 24-hour window.

Requires the machine to **sleep, not be powered off** (from off, you need a BIOS RTC alarm or
wake-on-LAN from another device). A laptop on battery generally will not wake; a desktop on mains will.

**Its weakness is silent failure.** A Windows update changes the sleep settings, a cable comes loose, the
power goes out for half a day — the wake does not happen, the window passes, and nothing says so. Cheap
mitigation worth building with it: the app knows the timestamp of the last message it processed, so a gap
of more than a day can be shown prominently on the dashboard. That does not prevent the loss; it stops it
being invisible.

Test it by setting two wakes and going away for a weekend. Cheaper than every alternative here.

## 2. A tunnel for phone access — free, and it changes the hosting maths

**Cloudflare Tunnel or Tailscale.** The dashboard becomes reachable from a phone with the application
staying at home and **no inbound port open**. This is the finding that removed hosting's main advantage,
and it is worth remembering before anyone re-opens that argument: remote access does not require moving
the application.

Note `UseForwardedHeaders` (recorded separately above) becomes load-bearing the moment a tunnel
terminates TLS in front of the app.

## 3. The capture relay — AWS Lambda Function URL + DynamoDB, $0/month

Telegram webhooks post to a small always-on function that appends to a queue; the home app drains it
outbound over HTTPS. Full design in `OPEN-QUESTIONS.md` P1-6 — the shape, the three routes, the
`secret_token` that keeps the endpoint from being an open "add an expense" API, and the cutover order.

Costs nothing at ~20 messages/day: Lambda's 1M requests and 400k GB-seconds and DynamoDB's 25 WCU/25
RCU/25 GB are permanent allowances, not trial credits. **Use a Function URL, not API Gateway** — the
gateway's free tier lasts 12 months and a research brief built a "$0" claim on top of it.

The bot token never leaves the PC in this design, so the settled secrets rule survives; the price is that
the "saved" acknowledgement arrives late, when the machine returns.

**It narrows the gap, it does not close it.** The same 24-hour buffer applies to webhook mode, and
Telegram retries a failing webhook for an undocumented number of attempts. It removes the PC's uptime
from the equation — a large reduction, not a guarantee.

Platform barely matters at this size: GCP Cloud Run functions + Firestore, Azure Functions Consumption,
Cloudflare Workers + Queues and Deno Deploy all sit two to three orders of magnitude inside their free
tiers. Cloudflare's Queues pull/ack API is the best *shape* — it is literally the drain and ack endpoints
— but it is TypeScript only, a second toolchain for ~100 lines.

## 4. Hosting the whole application — €5–6/month, and probably never needed

Verified floor, cheapest first. Watch the terms: the advertised prices usually assume long prepayment.

| Option | Price | Terms |
|---|---|---|
| OVHcloud VPS-1, 4 GB | **$4.54/mo** | 12-month prepay; no monthly rate offered |
| Contabo Cloud VPS 4, 8 GB / 4 vCPU | **€5.50/mo** | 24-month effective rate, incl. VAT |
| netcup VPS 500 G12, 4 GB | €5.91/mo (12-month) or **€7.30/mo hourly, no lock-in** | |
| Hetzner CX23, 4 GB | €5.99/mo incl. IPv4 | **out of stock on 2026-09-21** |
| Oracle Cloud Always Free (Ampere ARM) | **$0** | Ampere is listed as Always Free; the OCPU/RAM figures are JS-injected and could not be verified. Capacity is chronically short |

**Ruled out, with reasons worth keeping:**

- **GCP Cloud Run** (~$52.60/mo with a warm instance) — it caps a WebSocket at **60 minutes**, so a
  Blazor Server circuit force-reconnects every hour. Disqualified on behaviour, not price.
- **Azure Container Apps** — $73.44/mo US, $102.82/mo Europe. An earlier "$63" counted the vCPU and
  omitted memory.
- **AWS Fargate** — $36.04/mo for 1 vCPU / 2 GB; a load balancer is not required (a task can take a
  public IP), so the "+$16 ALB" that made it look like $50 was wrongly generalised.
- **Cloudflare Containers** — the instance sleeps after a timeout. Anything that sleeps drops the Blazor
  circuit and is disqualified outright.
- **Render and Koyeb free tiers** — Render spins down after 15 minutes idle and expires a free database
  after 30 days; Koyeb caps a free database at 5 active hours per month.
- **AWS App Runner** — stopped accepting new customers on 2026-04-30.

**What hosting costs beyond money**, and why it is last on this list: `ProtectKeysWithDpapi()` is
Windows-only and a certificate-protected key ring **cannot decrypt what DPAPI wrote**, so every stored
secret must be re-entered; the Linux port is 2–4 days; and a public-repo finance application ends up on
the open internet with no rate limiting on its login.

## `LedgerConnectionString`'s `NOOF_TEST_PG` fallback was a landmine for a locally launched publish output — fixed 2026-09-22, commit `59e4783`

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
test-suite convenience (`docs/OPEN-QUESTIONS.md` / `ops/reset-database-auth.ps1`), and the fallback
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

## Two lessons about researching prices, kept deliberately

**Vendor marketing pages frequently do not render prices to a fetch.** Azure's shows `$-` thirty-six
times; Hetzner's homepage widget shows none at all; Oracle's spec table is injected by JavaScript. This
is exactly how wrong numbers get quoted from memory and then cited to a page that does not contain them.
Azure's real prices are in the **retail prices API** (`prices.azure.com/api/retail/prices`) as plain JSON.

**Check whether a free tier is permanent or a 12-month trial.** That distinction flipped one
recommendation entirely, and it is invisible in every comparison article.

---

# Choosing the default currency from Telegram

**Status:** deferred by the operator, 2026-09-22. A single hard default (`RSD`) ships instead.

A message that states no currency — `кофе 250` — has to become money in some currency. Until this
lands, that is always `CategorizationWorkerOptions.DefaultCurrency`, which is `RSD`.

**What was actually wrong before the default existed**, and why this is not a nice-to-have: `currency`
was a `required` property in the response schema, so constrained decoding *forced* the model to emit
one of the five codes whether or not the message said anything. The model had no way to say "not
stated" and no way to be right except by luck. The amount was verified against the raw text and the
currency beside it was a guess — with a hundredfold consequence between RSD and EUR. Making the
property optional and substituting a known default is what removed the guess; the Telegram command
below only makes the default the operator's to choose.

**What to build.** A bot command — `/currency eur`, or a one-tap keyboard — that sets the default for
every later message that omits one. It should:

- store the choice, not hold it in configuration, so it survives a restart and is visible on the
  settings page next to the other operator-owned values;
- apply only to messages captured *after* the change, never retroactively — a transaction already
  recorded in RSD was recorded in RSD, and re-interpreting history on a setting change is the same
  class of mistake as bucketing a day by the current time zone rather than the row's (decision P1-3);
- confirm the change in the chat, so the operator can see it took effect without opening the dashboard.

**Where the default lives today, and where it should move.** `CategorizationWorkerOptions.DefaultCurrency`,
bound from the `Categorization` configuration section. When this item is built it becomes a stored
value the bot command writes and the worker reads per job, and the configuration key should be removed
rather than left as a second source of truth that silently disagrees.

**The alternative not taken, recorded so it is not re-proposed as new.** The default could have been
derived from the wallet the capture lands in — wallets already carry a currency, and Phase 2 gives
every account one wallet per currency. It was not taken because the operator asked for a chosen
default rather than an inferred one, and because a wallet-derived default cannot express "I am
travelling, price things in EUR for now" without moving the whole capture to a different wallet.
Worth revisiting when Phase 2 makes multi-wallet capture real.
---

# Two things the closing review flagged and could not settle

Recorded 2026-09-22 by the Phase 1B closing review, which ran on a different model family from the
one that wrote the code. Neither is a proven defect; both are cheap to check and expensive to
discover the hard way.

## Does any dependency throw an OperationCanceledException that is not our stopping token?

`CategorizationWorker` and `TelegramPollingService` both keep the host alive by catching everything
*except* `OperationCanceledException`, which they let through because it means "we are shutting down".
`BackgroundServiceExceptionBehavior` is not overridden, so its .NET 10 default of `StopHost` applies:
anything that escapes `ExecuteAsync` takes the whole application down, the other poller included.

That is safe only while no dependency raises an `OperationCanceledException` for a reason other than
our own token. `AnthropicCategorizer` converts its timeout deliberately. The reviewer believed
`Telegram.Bot` 22 wraps a request timeout in its own `RequestException` but **did not verify it
against the package**, and a `TaskCanceledException` from `IChatNotifier` would reach the outer catch
as an `OperationCanceledException` and stop the host.

**To settle it:** drive `IChatNotifier.EditAsync` into a real timeout and see what type comes out. If
it is an `OperationCanceledException`, the filter needs to distinguish our token from anyone else's —
`ex is OperationCanceledException && stoppingToken.IsCancellationRequested` rather than a bare type
test.

**Still open after Phase 5 (2026-09-25).** Phase 5 made every hosted loop catch non-cancellation
exceptions per tick and await `IDatabaseGate` first (`CLAUDE.md` §4), but the filter itself is
unchanged — still a bare `ex is not OperationCanceledException` — and
`HostOptions.BackgroundServiceExceptionBehavior` is still the unoverridden .NET default `StopHost`
(confirmed directly by Phase 5's C-1 finding, `docs/OPEN-QUESTIONS.md` P5-1). A dependency that raises
`OperationCanceledException` for a reason other than the loop's own token would still stop the host.
The verification step above is still not done.

## The read model's "current zone" is supplied by a registered singleton now — a test gap remains

**Corrected 2026-09-22 by Phase 1C Task 2.** This entry originally said the zone is "never given a
zone other than UTC". That was true of every test but overstated as a claim about production, which
is worth restating precisely now the wiring under it has changed.

`EfSpendingReadModel` takes the operator's current zone as a constructor argument and compares it
against each row's own stored zone — that pairing is the whole point of decision P1-3, and it is what
keeps a spend made in Belgrade on its Belgrade day after the operator moves. Before Phase 1C that
argument was threaded through a hand-written three-argument lambda registration in `Program.cs`.
Phase 1C Task 2 registers `TimeZoneInfo` itself as a singleton (`CaptureTimeZoneGuard.Resolve(...)`
against `Capture:TimeZone`, alongside the existing `AddSingleton(TimeProvider.System)`), and
`AddNoofPersistence` registers `EfSpendingReadModel` by type, resolving `currentZone` from the
container — so in production the read model now gets the operator's real configured zone, not UTC.

What has not changed: `EfSpendingReadModelTests` still constructs `EfSpendingReadModel` directly in
every test (`new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc)`), bypassing DI,
so the original gap stands unchanged — the seam between "the zone the month boundaries are computed
in" and "the zone each row is bucketed by" is still never exercised with two different values. The
row-zone side is covered well (there is a test built so that a naive `date_trunc` would merge two
months the correct query separates). No defect is known; it is untested, not known-wrong.

**To settle it:** one test with `currentZone` set to something well away from UTC and rows carrying a
third zone, asserting which month each lands in.

---

# Phase 1C measurements — accessibility and composition analyzer choices

Recorded 2026-09-22 closing Phase 1C, so none of these gets re-proposed as new without the measurement
that already settled it.

## `AnalysisMode=All` was measured and rejected

A full rebuild with `-p:AnalysisMode=All` was run against the whole solution while Phase 1C looked for
an analyzer gate (requirement 4). It emitted **842 distinct warnings across 31 rules** — 376 `CA1707`
(underscores in names, this repository's deliberate test-naming convention, e.g.
`Every_routable_page_declares_its_authorization`), 258 `CA2007` (`ConfigureAwait`, meaningless in an
application with no synchronization context), 61 `CA2000`, and 28 `CA1062` (argument-null boilerplate
`CLAUDE.md` §3 forbids).

Adopting it would mean suppressing most of the rulebook and calling what survives "strictness" — the
opposite of a curated gate. Phase 1C Task 6 enabled a small named list instead (`CA1515`, `CA1852`,
`CA1862`, `CA1861`, `CA2263`, `IDE0005`), each chosen because the `AnalysisMode=All` run showed it cost
fewer than a handful of genuine fixes.

## `Microsoft.CodeAnalysis.PublicApiAnalyzers` was considered and deferred

It would lock public surface at build time via a hand-maintained `PublicAPI.Unshipped.txt` per
project — several hundred lines for `Domain` plus `Application` alone, whose public surface is
deliberately large because it *is* the cross-assembly contract. At type level it duplicates what
`PublicSurfaceTests` (added this phase) already does with no package at all; at member level it would
close a real gap that `jb inspectcode` (Tasks 7–8) now covers instead, as a periodic sweep rather than
a build gate. The maintenance cost of the unshipped files was judged not worth paying twice.

## `ops/inspect.ps1` reports findings under `tests\` that it deliberately does not gate on

A run against the finished tree reported **84 findings under `tests\`**, 67 of them
`ClassCanBeSealed.Global` against xUnit fixtures. Phase 1C requirement 1 exempts tests
(«Исключение - тесты. Для них можно делать internal или private protected.»), and xUnit fixtures are
routinely left unsealed or subclassed for reasons the inspection cannot see — gating on a count that
can never reach zero guards nothing. `ops/inspect.ps1` prints the count and fails only on ERROR-severity
findings under `src\`. Worth a look occasionally; not worth a gate that can never pass.

## ReSharper's other 42 WARNING-level findings were seen and not triaged

A pre-settings baseline run of `jb inspectcode` found 42 issues at `WARNING` severity or above that are
not about accessibility (accessibility is handled separately, raised to ERROR): 18 `InconsistentNaming`
(ReSharper wants `_camelCase` private fields, which this repository does not use — a settings decision
somebody should make deliberately rather than by silence), 12 `AccessToDisposedClosure`, 4
`FormatStringProblem` in `EfCategorizationStore.cs` and `EfJobQueue.cs` (probably false positives about
raw SQL parameters, not checked), 3 `RedundantSuppressNullableWarningExpression`, 2
`ParameterHidesPrimaryConstructorParameter`, 2 `NotAccessedPositionalProperty.Global`, and 1
`UsingStatementResourceInitialization`. Out of Phase 1C's scope (accessibility and composition, not
general code health); none is gated at ERROR by `NoofLedger.sln.DotSettings`.

## `PublicSurfaceTests`'s regex does not match `public delegate`

The allowlist test's `TopLevelPublicType` regex
(`tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`) matches `class`, `record`, `interface`,
`enum` and `struct` declarations, but not `delegate`. There are no delegates in the tree today, so the
lock is complete in practice — but a `public delegate` added to a scanned project later would slip past
the allowlist unnoticed, silently defeating the point of the guard (widening the surface is supposed to
be a reviewed edit to `PublicSurfaceTests.Allowed`). A one-line regex fix, worth doing with a
watch-it-fail-first check the day the first delegate is actually added.

## `Noof.Ledger.E2E.Tests` was not in `NoofLedger.slnx` — fixed 2026-09-22

Discovered in Phase 1C when an unused-`using` error in
`tests/Noof.Ledger.E2E.Tests/CookieModeHostFixture.cs` survived a clean `dotnet build
NoofLedger.slnx`: the E2E project was not a member of the `.slnx`, so neither `dotnet build
NoofLedger.slnx` nor `dotnet test --solution NoofLedger.slnx` ever touched it, and `ops/publish.ps1`
— which only runs `dotnet test --solution` — did not cover it either. A test suite outside the
solution is a test suite that rots without anybody noticing.

Added to the `.slnx` at the operator's instruction, with both consequences accepted knowingly:
`dotnet test --solution` now runs the browser suite too, so a plain test run needs Playwright's
Chromium present and takes about 40 seconds longer, and the solution test count now includes those
13. Every number in this repository's documentation counts the whole solution from here on.

## The Persistence test suite takes 1m48s and nobody has found where it goes

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

## The test template can silently drift from what migrations produce — fixed 2026-09-22

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

## MudBlazor features that need a render-mode decision first — deferred 2026-09-22

Phase 1D adopted MudBlazor but deliberately uses none of its JavaScript-backed components. MudBlazor
documents that its providers "must render in the same interactive render mode as the components that
use them", and `MainLayout` renders statically because render modes are per-page — which is itself
forced by the sign-in page being a real form POST. So there is no `MudPopoverProvider`,
`MudDialogProvider` or `MudSnackbarProvider`, and therefore no popover, dialog, snackbar, tooltip,
menu, `MudSelect`, `MudDatePicker` or `MudAutocomplete` anywhere in the app.

Three ways out, when something actually needs one:

1. **Put the providers on each interactive page.** MudBlazor's own documented answer for per-page
   interactivity. Cheapest, and the duplication is two lines per page.
2. **Go globally interactive** (`<Routes @rendermode="InteractiveServer" />`) and mark the sign-in
   page `[ExcludeFromInteractiveRouting]`. Cleaner afterwards, but it makes every page hold a
   SignalR circuit, including the dashboard, which today needs none.
3. **Keep doing without.** A single-user local ledger with four screens has not yet wanted a dialog.

Nothing is blocked today. This is written down so the next person who reaches for `MudDialogService`
and finds it silently doing nothing knows why in one minute rather than one afternoon.

## A light/dark toggle — deferred 2026-09-22

The theme is fixed dark, set as a parameter on `MudThemeProvider`. A toggle needs interactivity plus
somewhere to persist the choice, and the layout is static (see above). Switching the whole app to
light is a one-line change to `NoofTheme`; offering the user the choice is not.

## The dashboard's reading width — deferred 2026-09-22

`MudContainer MaxWidth="MaxWidth.Large"` caps the content at 1280px. On a 2552px monitor that leaves
wide empty margins, which looks under-filled for a dashboard; on a laptop it is exactly right. The
honest fix is not a bigger number but a layout that uses the extra width — three cards across
instead of two — and that is worth doing when there are more than two currencies to show.

---

## Editing a transaction in the dashboard

**Wanted.** Change the amount, date, category or merchant of a record from the web UI, or cancel it
there.

**Why it is not in Phase 2.** Phase 2 makes Telegram the place to correct a record — reply, the
Изменить button, or editing the original message — because that is where the operator already is when
the echo arrives. The dashboard edit is a second surface over the same revision history.

**Cost already paid.** `transaction_revisions` records every state, and `CategorizationAuthority.User`
already outranks the model, so a dashboard edit is a page plus a revision row, not a schema change.

---

## Rolling back to an earlier revision

**Wanted.** "Undo that correction" — restore the record as it was before the last change.

**Why it is not in Phase 2.** Cancel/restore covers the case that matters most; `transaction_revisions`
keeps every state so rollback needs no migration when it is built.

---

## Pressing Изменить twice forgets the first prompt

`transactions.prompt_message_id` holds one prompt. A reply to an older, superseded prompt is not
recognised and is captured as a new message. Rare, visible in the chat, and fixed by a small table of
prompts if it ever matters.

## An edited original whose re-reading fails leaves no revision of the new text

`ReplaceRawTextAsync` changes `raw_text` and queues a re-reading; the Edit revision is written when that
reading is applied. If it fails, the new text lives only on the transaction row. The previous state is
still in the history.

---

## Loose ends from the Phase 2 closing review

Recorded 2026-09-23 by the Phase 2 closing review (Fable 5.1, a different model family from the one
that wrote the code). Each below is a real, cheap-to-verify gap rather than a proven defect; none
blocks the phase.

**`AddCorrectionJobs` dropped the plain index on `categorization_jobs.transaction_id`.** EF's
FK-index convention does not notice the filtered unique index the migration added in its place, so
`transaction_id` now has a filtered unique index but no plain one. Consequence: a per-candidate scan
inside `ClaimAsync`'s `NOT EXISTS`, and an unindexed CASCADE FK — irrelevant at personal-ledger scale.
If it ever matters, add an explicit `HasIndex(j => j.TransactionId)` in a new migration.

**The per-transaction ordering guard has an untested branch.**
`tests/Noof.Ledger.Persistence.Tests/EfJobQueueTests.cs:363-388` covers only "an earlier `Pending` job
blocks a later one for the same transaction". Untested: an earlier job `Claimed` (in flight) also
blocking a later one — the case that actually prevents the double-application race — and "an earlier
`Failed` job does NOT block". A future rewrite that compares against `status = 0` directly would keep
this test green while losing the guarantee it names. Cheap to add both rows.

**A reply to a stale bot message is captured as a new transaction.**
`src/Noof.Ledger.Telegram/CorrectionHandler.cs:13-23` and `TelegramUpdateRouter.cs:44-46` — a reply
to a bot message that matches no record falls through to capture as a **new** spend. Realistic
triggers: answering an earlier Изменить prompt after a second tap replaced `PromptMessageId` (only the
latest prompt is remembered — see "Pressing Изменить twice" above), or replying to the poison notice.
*"нет, 1500"* then becomes a fresh transaction the model categorises. The echo/cancel net still
catches it, and the behaviour is pinned by design (a reply to anything but an echo is captured as a
new message). Suggested refinement, not built: when `repliedTo.From?.IsBot == true` and nothing
matches, answer "не нашёл запись" instead of capturing.

**`Исправляю…` can race the correction's own echo.**
`src/Noof.Ledger.Telegram/CorrectionHandler.cs:18-22` sends its "Исправляю…" edit after the
correction job is already queued. If the worker claims the job, calls the model and echoes before
that edit lands (slow Telegram, fast model), the final echo is briefly overwritten with
"Исправляю…" and no buttons until the next tap or reply. The database is correct throughout; it
self-heals on the next interaction.

**`AskAsync` can fail if the echo it replies to was deleted.**
`src/Noof.Ledger.Telegram/TelegramChatNotifier.cs:31-38` — the Изменить prompt replies to the echo
message. If the person deleted the echo, Telegram refuses ("message to be replied not found") and the
update burns three poison attempts for no reason. Consider
`ReplyParameters.AllowSendingWithoutReply = true`.

---

## Currencies outside the five known codes

**Wanted.** A sixth currency code, or at least a clean failure when one is meant, instead of the
silent RSD default.

**Why it is not scheduled.** `record_spending`'s (renamed to `record_transaction` in Phase 4) response schema constrains `currency` to a
compile-time enum of the five supported codes, so the model has no way to answer with a sixth even
when a message names one — it lands on `CategorizationWorkerOptions.DefaultCurrency` (RSD) instead,
visible only in the echo if the operator happens to notice the wrong code. Widening it safely is
Phase 4/7 work: `CurrencyCode.Supported` conflates "nameable" (can appear as an ISO code at all) with
"rateable" (has an exchange-rate source), and Phase 7's rate source (`docs/OPEN-QUESTIONS.md` Q4) is
scoped to the same five. Recorded by the operator's 2026-09-23 review (P2-5, D-F).

**What it costs, when built.** Split `CurrencyCode.Supported` into a nameable set and a rateable
subset, widen the schema enum to the nameable set, and reopen Q4 for whichever currencies gain a rate
source.

## Multi-language bot

**Wanted.** The bot's own text (the echo, buttons, prompts) in more than one language.

**Why it is not scheduled.** English only for now, by the operator's 2026-09-23 review (P2-5, D-D) —
multi-language was never budgeted into Phase 2, and shipping one language today costs nothing toward
shipping more later. The operator may still write to the bot in any language; only the bot's own
output is fixed to English (`Category.NameEn` in the echo, `RecordEcho`'s constants, the button
labels).

**What it would take.** Resource strings per language instead of literals in `RecordEcho.cs` and
`RecordActionButtons.cs`, and a language setting for the operator to choose from — stored, not
configuration, the same shape as the currency default above. `Category.NameRu` already exists in the
schema and is unused by the bot today, so the category half of this is data that is already there.

## Vocabulary hints for the transcriber

**Wanted.** Fewer misheard merchant names in a voice transcript.

**Why it is not scheduled.** Groq takes a `prompt` of up to 224 tokens. Merchant names from the
directory would help it spell *Maxi*, *Lidl* and *Wolt*. That sends a slice of the shopping profile to
Groq, the same trade as Q7 (`docs/OPEN-QUESTIONS.md`). It belongs to Phase 11 calibration, once there
is a real-voice corpus to judge it against.

## Keeping the audio

**Wanted.** A re-transcription corpus, to compare providers or Whisper versions on real voice notes
later.

**Why it is not scheduled.** Nothing stores the voice note itself. The `file_id` can fetch it again
while Telegram keeps it, which is enough for the current pipeline. A re-transcription corpus for Phase
11 would need the bytes in the database, which would then be in every backup. That is a privacy
decision for the operator, not a default to reach for.

## Whisper's inventions on silence

**Wanted.** Fewer fabricated transcripts from near-silent or noisy voice notes.

**Why it is not scheduled.** A near-silent note can come back as *«Продолжение следует…»* or
*«Субтитры сделал…»* — Whisper's own hallucination on low-signal audio. It is recorded as whatever the
model makes of it, and the `🎤 "…"` line shows it as-is. P2-1 forbids a filter on what the transcript
says. If it happens often in practice, the answer is a decision (for example, Groq's `verbose_json`
`no_speech_prob`), not a quiet guard added later.

## A second speech provider

**Wanted.** A fallback when Groq is unavailable or its terms change.

**Why it is not scheduled.** Groq publishes no SLA, which is a known trade-off from P3-1, accepted for
now. `ISpeechToTextClientFactory` (V2) is the seam a fallback would use, and nothing uses one yet — no
demonstrated need.

---

## Loose ends from the Phase 3 closing review

Recorded 2026-09-24 by the Phase 3 closing review (Fable 5.1, a different model family from the one
that wrote the code). Both are real, cheap-to-verify UX gaps rather than proven defects; neither blocks
the phase.

**The dashboard names the wrong stage for a voice record awaiting or missing its transcript.**
`RecentTransaction` (`src/Noof.Ledger.Application/Reporting/ISpendingReadModel.cs`) carries no capture
kind, so `Home.razor` cannot tell a still-transcribing voice note from a captured text message: a voice
note with no transcript yet shows an empty description under "Awaiting categorisation - this message
has not been read by the categoriser yet" (`src/Noof.Ledger.Web/Components/Pages/Home.razor:111-113`),
and a "Heard nothing" record (`RecordEcho.HeardNothing`,
`src/Noof.Ledger.Application/Chat/RecordEcho.cs:19`) shows "Categorisation failed. Nothing was recorded
for this message." (`Home.razor:115-119`) — a `Failed` transaction status, which this is not; the audio
was heard, it just held no speech. Fix later by carrying `CaptureKind` (and, for the second case, the
distinction between "never categorised" and "heard nothing") into `RecentTransaction`, and phrasing the
two states for what actually happened.

**A spoken correction's own transcript is never shown.** `RecordEcho.WithWhatWasHeard`
(`src/Noof.Ledger.Application/Chat/RecordEcho.cs:54-57`) prepends `🎤 "{record.RawText}"` from the
transaction's original transcript only — `RawText` is written once, by `CompleteCaptureAsync`'s
`IS NULL` guard, and never again. A correction spoken as a reply stores its own transcript in
`CategorizationJob.Instruction`, which the final echo never surfaces: after a spoken correction, the
echo shows the *original* 🎤 line and the corrected body, but not what the correction itself was heard
as — exactly the ambiguity V5 was built to remove, just not for this path. A fix would prepend
🎤 "<instruction>" when the latest revision is a spoken correction; deferred because it needs a small
spec decision from the operator (which revision's transcript to show, and how it composes with the
original 🎤 line already shown above the body).

---

## Deferred from Phase 4 (money model and backup)

**Cross-currency conversion.** A spend in a currency other than its wallet's own (M10) is recorded as
a separate currency line on that wallet's balance, not converted. Building this needs a rate source
decision (Q4 in the original design's open questions) the operator has not made, and a rate is a
moving target that would need its own history to stay honest in a re-read old transaction. Not
scheduled until a rate source is chosen.

**Transfers between wallets (Phase 7).** `TransactionKind.Transfer = 3` and `EntryRole.Fee` are
reserved values, not declared members of the enum (`MoneyModelEnumTests` pins the current member
counts) — a transfer becomes two entries (one per wallet) with no schema change needed when that
phase arrives. Moving cash between wallets today is two separate manual transactions (an expense
from one, an income to the other), which loses the "this was the same money" relationship a real
transfer would keep.

**Loans are recorded as other-income until Phase 7 models transfers/liabilities.** A loan received
("заняла у Маши 5000 рсд") is recorded as kind `income` under the `other-income` category
(`CategorizationPrompt`'s I-3 fix, Phase 4 final review) so the wallet matches the bank — but a loan
is a liability, not earned income, and there is no `Transfer`/liability kind yet to record it more
precisely. Until Phase 7, this means the dashboard's income totals include money that was borrowed,
not earned. Revisit once transfers (above) are built.

**The Recent list shows income and opening balances indistinguishable from spending.**
`RecentTransaction` (`src/Noof.Ledger.Application/Reporting/ISpendingReadModel.cs`) carries no
`Kind`, so a 2000 EUR salary and a wallet's "Opening balance" checkpoint appear in the dashboard's
"Recent" list exactly like an expense — money is not wrong ("This month" is filtered by kind), but a
reader cannot tell +2000 from −2000 at a glance (M-6, Phase 4 final review). Fix by carrying `Kind`
into `RecentTransaction` and giving the list a marker (a chip, a sign) per row.

**A correction to a record whose wallet was archived no longer falls back to the default (resolved
2026-09-27, PR #3).** M-7 (Phase 4 final review) is reversed: a correction that names no wallet now
keeps the record's existing wallet even after that wallet is archived — archiving must not rewrite a
historical record's wallet out from under it. See `KeepingTheRecordsWallet` and
`WalletsIncludingKept` in `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`.

**The same-day checkpoint ordering edge.** A purchase dated to the same local day as a balance
statement, but sent to the bot after the statement, is ordered after it (M6's `(occurred_on,
occurred_at)` rule) — so the *next* statement absorbs it instead of the one it was dated alongside.
This is a known, accepted approximation (recorded in the spec's "Known limits"), not a bug: the
alternative (ordering by `occurred_on` alone, ties broken arbitrarily) would make a statement's
"adjustment" figure depend on transcription order rather than anything the operator said.

**Encrypted backups.** `BackupWorker`'s dumps sit unencrypted under `%LOCALAPPDATA%\NoofLedger\backups`,
protected only by the user profile's own permissions — the same trust boundary the credential file
already relies on. OneDrive sync (Q8 in the original design) is Phase 10 and would want this decided
first, since syncing an unencrypted financial dump to the cloud is a different risk than a dump that
never leaves the machine.

**A separate PostgreSQL instance for tests** (own port, `fsync` off, no real data on it). Phase 4's
subagents spent most of their time in database test runs and in waiting on the shared suite lock:
every worktree's `DROP DATABASE` waits on the one server's checkpoints, and under parallel load the
fixtures' cleanup timed out and failed tests that were not broken. A test-only cluster pointed at
through the existing `NOOF_TEST_PG` variable would make clones cheap, retire the lock, and keep test
clones off the server that holds `noof_ledger`. Costs a second cluster to start after a reboot and a
small ops script. The operator has seen the trade-offs (2026-09-24) and not decided; the Phase 4
rule of running database and E2E tests filtered, and in full once per phase, removed most of the
contention in the meantime, and Phase 6's `FILE_COPY` clones removed the long checkpoint waits
themselves (the 2026-09-27 entry below), which weakens the case for a second cluster.

**A cancelled dump can be recorded as a failed run.** `PgDumpDatabaseDumper` kills `pg_dump` on
cancellation with `if (!process.HasExited) process.Kill(entireProcessTree: true)`; if `pg_dump` exits
between the check and the kill, `Kill` throws `InvalidOperationException`, which replaces the pending
cancellation, and `BackupWorker` records an ordinary failed run. A microsecond window at host
shutdown, no data lost — wrap the `Kill` in a `catch (InvalidOperationException)` when next in the
file (Phase 4 fix-wave re-review).

---

## Loose ends from Phase 5 (observability)

Recorded 2026-09-25 closing Phase 5. The critical and important findings from the Fable 5.1 closing
review were fixed on-branch; these are the minors the operator chose to park rather than fix, plus
two items the spec explicitly deferred.

**Proactive Telegram alerts when a health check turns red.** Today the operator only learns of a
failing check by opening the dashboard, `/diagnostics`, or asking the bot's `/health` — nothing pushes
a message when a check's state changes. Named out of scope by the observability spec (§"Out of
scope"); it would want a debounce (a flapping check should not spam) and a decision about which
checks are worth a push at all.

**M-1 — `SelfLog.Enable` attributes every non-connection Serilog self-log line to the database sink.**
**Closed 2026-09-26 (Phase 6), cross-host half; the message-type half remains, see below.**
`LoggingSetup.cs` still records *any* Serilog internal error that doesn't name a specific host:port (a
locked log file, a console write failure, not only a PostgreSQL batch failure) as a Log sink failure,
so the check can say "logs are in the file only" for the wrong reason. The other half of this item -
`SelfLog` being a process-global listener while `Configure` runs per host, so an orphaned host (the
throwaway one `WebApplicationFactory`'s `HostFactoryResolver` builds and never disposes) could feed a
later, unrelated host's `ILogSinkStatus` - is fixed: `SelfLogOwnership` (Phase 6 final tidy-up, item 3)
tracks the current claim and drops any "Failed to connect to `<host:port>`" message that names a
*different* connection than the current claim's own, closing the cross-host case
`SelfLogSinkFailureTests` flaked on. What remains is purely the message-type gap: a non-connection
failure (naming no host:port at all) still defaults to attributing to whoever is current. Fix: attach
to the PostgreSQL sink's own failure listener if `Serilog.Sinks.Postgresql.Alternative` exposes one,
otherwise filter `SelfLog` text by that sink's type name before recording a failure.

**Fixed (Phase 6 second re-review, R2-1) — `SelfLogOwnership`'s host:port comparison was wrong for any
connection string that named a host rather than an IP literal.** The old `HostPortOf` built the identity
from `NpgsqlConnectionStringBuilder.Host`+`Port` verbatim (`localhost:5432`), but Npgsql's own failure
message always names the *resolved* IP endpoint (`Failed to connect to 127.0.0.1:5432`), never the
configured host name — verified directly against Npgsql 10.0.3. `IdentitiesOf` now resolves the identity
the way Npgsql renders it: an IP literal (v4 or v6, `IPAddress.TryParse`) is used as-is — an IPv6 literal
renders bracketed, `[::1]:5432` — and any other host is resolved once per claim through
`Dns.GetHostAddresses`, keeping every resulting `IPEndPoint`; a Unix-socket path or a failed resolution
carries no identity and falls back to defaulting every failure to the current claim, same as a message
naming no connection at all. `SelfLogOwnershipTests` covers a host name resolving to loopback, an IPv6
literal host, and a foreign endpoint still being rejected.

**New from the same fix.** The endpoint identity is resolved once, in `Claim`, at process startup — not
re-resolved for the life of the process. A remote database host whose DNS answer changes while the host
is running would keep comparing against the address it resolved to at startup, so its failures would be
dropped again, the same silent gap M-1 exists to close. Acceptable for this local-hosting deployment —
`ops/reset-database-auth.ps1` writes `127.0.0.1`, an IP literal with no DNS involved at all — revisit if
the database ever moves off-box to a host name whose address can actually change underneath a running
process.

**M-3 — three near-identical registration entry points for one folder.**
`DiagnosticsRegistration.AddNoofDiagnostics`, `DiagnosticsHostRegistration.AddNoofDiagnosticsHost` and
`HostDiagnosticsRegistration.AddNoofHostDiagnostics(connectionString)` are all called from `Program.cs`
and all live under `Noof.Ledger.Host/Diagnostics`. Harmless today; collapsing them into one
`AddNoofHostDiagnostics(connectionString)` is ordinary tidying whenever that folder is touched next.

**M-10 — the Database health check reads the gate, not PostgreSQL, so it can say "Ready" while
PostgreSQL is down.** `DatabaseHealthCheck` reports whatever `IDatabaseGate.State` was when it last
changed; once `Ready`, it never re-probes, so an outage that starts *after* startup shows "Database —
Ready" in green next to Migrations, Backup and Telegram failing with "Check failed (NpgsqlException)
— see logs" underneath.
Spec-conformant (the design says gate `Ready` → Ok) but confusing to read. A live `SELECT 1` with a
short timeout on every check would be better and is cheap; not built because the gate's own workers
already recover on their own, so nothing operationally depends on this check being live.

**M-11 — `/health`'s command registration is not retried, and `/health@otherbot` is also accepted.**
`setMyCommands` (which registers `/health` in the owner's chat) runs only when the bot token changes;
a transient network failure at that moment is logged and never retried until the next restart. Separately,
the router accepts `/health@anyname`, not just `/health@<this bot's own username>` — harmless for a
single-owner DM bot where nothing else is listening, so left as is rather than plumbing the bot's own
username through for a check that changes nothing observable.

**Paths deliberately left untimed (Phase 5, task V12).** `IOperationTimer` covers the model, speech,
Telegram, worker and health paths named in the verbose logging design, but not: `EfCaptureStore`
(the initial message-received write); the Cancel/Edit/Restore button path (`RecordActionHandler`,
`CorrectionHandler`, `EfRecordEditor`); a separate timing for the revision-append inside
`RevisionLog.AppendAsync` (it is covered only as part of whichever timing wraps its caller); and
model token usage (`IOperationTimer` measures wall-clock time, not tokens — a separate metric).
Add any of these if a slow path shows up that the existing timings do not explain.

**Timing rows inside the trace timeline itself were considered and rejected for Phase 5.** Timing
events carry a `TransactionId` but no `Stage`, and `/transactions/{id}/trace` is built only from
Stage rows (`EfTransactionTrace`) — mixing timing rows into that view would clutter the one page meant
to answer "what happened to this message" at a glance, and timing rows only exist at all once the
database log level is at Debug. Instead the trace page links to `/diagnostics/logs` pre-filtered to
the transaction at Debug ("Timings and debug log"). Revisit only if the linked-page detour turns out
to be a real friction point in practice.

**`GroqHttpClientLoggingTests` does not exist.** `AnthropicHttpClientLoggingTests` and
`TelegramHttpClientLoggingTests` each prove `RemoveAllLoggers()` keeps that provider's `HttpClient`
from leaking request/response bodies (which could carry secrets) into the log pipeline; Groq's speech
`HttpClient` has no equivalent test, even though `GroqRegistration` applies the same
`RemoveAllLoggers()` call. Low risk today (nothing in the Groq request path carries an app secret),
but the gap is real and cheap to close whenever `Noof.Ledger.Ai/Groq` is next touched.

**FX freshness check arrives with the FX phase.** The observability spec named this out of scope
because there is no FX rate source yet (`docs/OPEN-QUESTIONS.md` Q4) — nothing to check the freshness
of. Add it alongside whichever phase builds currency conversion.

**Playwright cannot drive a native `datetime-local` widget in headless Chromium**, so
`/diagnostics/logs`'s From/To filter is verified through the query layer and the page's markup, not
through a browser interaction test. `FillAsync` sets the DOM value but fires no event Blazor Server's
`@bind:event="oninput"` receives — confirmed with a debug span that stayed empty across `oninput`,
`onchange`, a plain `@bind`, and an explicit bubbling `dispatchEvent`. Coverage instead: `EfLogQueryTests`
proves the SQL/EF filtering (inclusive both ends), and `DiagnosticsPageSourceTests` proves the page
renders `#logs-filter-from`/`#logs-filter-to` and wires them into the query. Revisit if a future
Playwright or Chromium release fixes native datetime input dispatch.

**An intermittent Host auth-flow test flake**, seen twice across roughly six `Host.Tests` runs during
the phase's closing fix pass, never reproduced on an immediate retry, and not connected to any finding
fixed in this phase (a clean 292/292 pass bracketed each sighting). The failing test's own name was not
captured — the run's tail buffer held only request-log noise by the time it was checked. Worth
instrumenting the next time it is seen live rather than chasing from this description.

### Test-infrastructure flakes seen at the Phase 5 close

- **Clone `CREATE`/`DROP DATABASE` timeouts in full runs — closed 2026-09-27 (Phase 6).**
  `CookieModeHostFixture.DropCloneAsync` and the Database-tagged Host.Tests classes hit 120 s
  `DatabaseSettings.ExecuteAdminDdlAsync` read timeouts: once at the Phase 5 close, then 11–23 entries
  per full run during Phase 6 — with a second checkout's suite running, and later with none. Raising
  the admin timeouts to 120 s did not help, and a concurrent checkout was only an amplifier. Sampling
  `pg_stat_activity` during a run showed the drops waiting on `IPC/CheckpointDone` and
  `ProcSignalBarrier` while a single checkpoint took over two minutes. The cause: every `DROP DATABASE`
  forces a checkpoint and waits for it, and under PostgreSQL's default `WAL_LOG` strategy each clone's
  ~300 files go through shared buffers, so every checkpoint had to fsync every clone created since the
  previous one. With 40 live clones, one `CHECKPOINT` took 42.6 s under `WAL_LOG` and 0.17 s under
  `FILE_COPY`, and creating the 40 cost about the same (33 s vs 36 s). `DatabaseSettings` now creates
  every test database `STRATEGY FILE_COPY` (commit `3b04301`, guarded by
  `DatabaseSettingsCloneStrategyTests`). `FILE_COPY` is slower for a clone dropped within seconds —
  a short-lived create/write/drop benchmark favoured `WAL_LOG` (38.5 s vs 62.7 s), because dropping a
  clone forgets its queued fsyncs — but a full run keeps Persistence's clones alive until collection
  teardown, and the whole-run outcome is what was measured: checkpoint sync time over a full run fell
  from 515 s to 42 s, and the slowest sampled DDL from the 120 s ceiling to 4 s. Two consecutive full
  `dotnet test --solution` runs then passed first time. The cost is time: Persistence takes 13 min alone
  and 14–15 min inside a full run under `FILE_COPY`, against about 12 min under `WAL_LOG` on the same
  clean server — the per-test database strategy entry above is where to win that back.
  The advisory lock around clone DDL that was once proposed here is not needed. Leftover
  clones from interrupted runs are harmless but still worth `.\run.ps1 clean-test-dbs`: 200 had built
  up, mostly from these timed-out drops.
- **`DatabaseLogLevelDbTests.A_stored_Off_level_drops_the_next_hosts_own_startup_burst...` failed once
  in five runs** (2026-09-26, same slow server): EF's "No migrations were applied" row reached
  `app_log` despite a stored Off. `ReadyGatedBufferSink` gives the stored-level load 5 s
  (`LoadTimeout`) before it flushes the startup buffer at the compiled-in default, so a load slower
  than that lets the startup burst through. It passed on an immediate rerun and was not changed in
  Phase 6. It failed once more on 2026-09-26 in a full run with the checkpoint stalls above, and
  passed in every run after the `FILE_COPY` fix. It recurred on 2026-09-27 in the full run after
  Copilot round 7, on a healthy server under full-suite load, with the same row, then passed 3 of 3
  when re-run alone. So this is a real timing race, not server degradation: the buffer times out to
  the compiled-in default before the stored level arrives. The fix is to make `ReadyGatedBufferSink`
  wait for the stored level, or for a definite "none stored", instead of flushing at a 5 s timeout.
- **`SecretRedactionSentinelTests` hits an `IOException` in its cleanup — closed 2026-09-26 (Phase 6)**,
  not its assertions:
  `Directory.Delete(logDirectory)` in the `finally` runs while the host's file sink still holds
  `noof-ledger-<date>.log`. Seen once at the Phase 5 close; on 2026-09-26 it reproduced 2 runs in 3
  when the class ran together with `SecretRedactorTests` straight through the xUnit exe, and passed
  alone and in every full `run.ps1 test all`. Parked by the operator as test-only (no effect on the
  running app); the fix is to dispose the factory before deleting, or retry the delete briefly.
  Phase 6 took the second option: the `finally` now deletes through `TestHostLogging.DeleteBestEffortAsync`, the
  same bounded retry `HostProcess.DeleteBestEffortAsync` uses for the out-of-process E2E host.
- **Configuration-added Serilog sinks did not reproduce through `WebApplicationFactory`** while they did
  in an isolated logger (follow-up review I-1). `ReadFrom.Configuration` is gone, so the risk is closed,
  but the reason the hosted repro stayed silent was never found.

### Cadence and timeout constants still in code — deferred 2026-09-26

PR #1's review asked for hard-coded paths to move to `appsettings.json`; paths, file-log limits,
per-level retention, every backup setting and the DataProtection key directory did. These stayed in
C# on purpose, because they are how often or how long, not where or how much, and nobody has needed
to change one: the worker poll intervals (`SecretSnapshotRefreshWorker`, `LogRetentionWorker`), the
health checks' staleness windows (Disk, Backup, Log sink, Telegram), `TelegramBackoff`'s caps, the
5 s per-check timeout in `SystemHealth`, and `pg_dump`'s `PGCONNECT_TIMEOUT`. Move one when a real
reason to tune it appears, through the options pattern the rest already use.

Phase 6 added its own three, swept for the Phase 5 convention pass and left in code for the same
reason: the `suf-purs` `HttpClient`'s 15 s timeout to the Tax Administration (`ReceiptsRegistration`),
the 5 s deferral `CategorizationWorker.TryRouteToReceiptAsync` waits before re-checking a receipt
that is still being extracted (`ReceiptExtractionPendingDelay`), and the 40-line cap on a receipt's
detailed echo (`RecordEcho.MaxDetailedReceiptLines`, a display decision, not a resource setting).

### `transaction_revisions` retention — deferred 2026-09-26 (decision (e))

Log settings (`/diagnostics/logs/settings`) gives the operator per-level retention for `app_log`, but
deliberately not for `transaction_revisions` — the append-only history a trace page's History section
reads. That table has no retention at all today: every revision, forever. Doing this properly needs,
first, the row-level trigger (`transaction_revisions_append_only_guard`, which refuses `UPDATE` and
`DELETE`; a sibling `transaction_revisions_no_truncate` separately refuses `TRUNCATE`) relaxed to
allow a retention job's own `DELETE`, which is a bigger decision than this feature's scope: it is the
one guarantee that table currently makes, and loosening it for one more caller is not something to do
as a side effect of a settings screen. Revisit alongside a real reason to prune old revisions (disk
growth becomes a real problem, or a GDPR-shaped request to actually forget something).
(The drift `.claude/rules/database.md` warns about — a guard trigger added to an already-applied
migration never taking effect on `noof_ledger` or the test template — was `merchant_aliases_no_truncate`
in commit `01b4961`, a different table; that story does not apply to `transaction_revisions`, whose
guards shipped in their own migration from the start.)

### Copilot review items parked by the operator — 2026-09-26

Copilot's second pass on PR #1 raised five items. The template-borne secret leak was fixed (a
library logging an interpolated string puts the text in the message template itself;
`SecretRedactor` now redacts the template too). Two described decisions the operator had already
made (Debug retention of 1 day; no file-tail fallback on the Logs page). The operator parked the
other two as not mattering in real use:

- **Secrets shorter than 8 characters are never redacted** (`SecretSnapshot.MinimumSecretLength`).
  Deliberate: a short value such as `1234` would be replaced everywhere it occurs in a log. The only
  secret this can realistically hit is a short database password — keep that password at 8+
  characters and the gap is closed. Lowering the cutoff just for the database password is the fix if
  that is ever not an option.
- **Most `WebApplicationFactory` fixtures still use the real DataProtection key ring**
  (`%LOCALAPPDATA%\NoofLedger\dp-keys`); only three call `UseTempKeyRingDirectory()`. A fixture only
  writes there when no valid default key exists — about once per 90-day key lifetime — and a key it
  creates is a valid member of the same DPAPI-protected ring, so the running app is unaffected. The
  fix mirrors the log directory: apply `TestHostDataProtection` everywhere and add an architecture
  guard like `TestHostLogDirectoryTests`.

---

## Deferred from Phase 6 (receipts)

Recorded 2026-09-26 closing Phase 6. Named explicitly out of scope by the spec, or found and parked
during implementation and its two closing reviews (Fable 5.1, `final-review.md`/`final-rereview.md`/
`final-rereview-2.md`).

**Exchange-office slips (Phase 7, R-1).** A currency exchange receipt is a different shape entirely — no
line items, no category per line, a rate instead — and belongs with whichever phase finally builds
cross-currency conversion (the deferred item above), not with the fiscal-receipt pipeline.

**A product → category dictionary as a cache in front of the model (R-Q7-adjacent).** Named out of
scope by the design as "possible later cache, like merchant aliases" — the same shape as
`IMerchantDirectory`'s write-once alias table, but keyed on a receipt line's product name rather than a
merchant. Would cut categorisation tokens on repeat purchases at the same shop; not built because there
is no real usage data yet to say which products repeat often enough to be worth caching.

**Editing receipt lines.** Named out of scope by the design (R-4's sibling: "storing photos" is
declined, editing lines is simply not built). A correction today can change a line's category or the
transaction's wallet, never a receipt line's name, quantity or price — those come from the fiscal
record or the vision read, and F-2 (`docs/OPEN-QUESTIONS.md` P6-1) answers a request to change one in
the echo rather than silently applying or ignoring it.

**Storing photos — declined, R-4.** The photo is never kept, in the database or anywhere in the repo;
only Telegram's `file_id` is stored, which only resolves while Telegram itself keeps the file. A
re-transcription or re-extraction corpus (the voice equivalent is recorded under Phase 3 above) would
need the bytes in the database, and therefore in every backup — the same privacy decision as keeping
voice audio, not a default to reach for.

**Bulk import.** Named out of scope by the design. Nothing about the receipt pipeline assumes one
receipt per message; a bulk path would be a new intake surface (a folder of photos, an export from
somewhere), not a change to extraction or categorisation.

**The Telegram 4096-character echo limit is handled by a fixed 40-line threshold, not a measured
length.** `RecordEcho.ComposeReceipt` lists the first 40 lines individually then groups the rest by
category once a receipt has more than 40 lines, rather than composing the full text, measuring it, and
falling back only if it would actually overflow. Deliberate (Task 5's report): the concrete rule the
design gives is the 40-line cutoff, no real receipt is long enough for the two paths to differ in
practice, and a two-pass measure-then-maybe-rebuild is the kind of ceremony `CLAUDE.md` §3 asks to skip
for a condition that cannot occur. Revisit if a receipt ever actually needs the distinction — a shop
with unusually long product names could in principle overflow within 40 lines.

**Fixed (Phase 6 second re-review, R2-3) — a correction deferred during a long extraction outage no
longer exhausts its retry budget silently.** (`docs/OPEN-QUESTIONS.md` P6-1's defer-mechanism note.) A
correction that arrives while a receipt is still being extracted is deferred on the job's own retry
budget (`MaxAttempts × 5s`, ≈40s) rather than discarded. `TryRouteToReceiptAsync`'s defer branch now
computes `isLastAttempt` the same way `HandleModelFailureAsync` does; on the final deferral it logs
Warning 1210 (`ReceiptCorrectionDeferralExhausted`), logs `StageFailed` for the `Categorized` stage the
same way every other terminal retry path does (so the trace page shows where the job died, not
nothing), and calls `NotifyFailureAsync` — which renders the `ComposeCorrectionFailure` echo in Telegram
and leaves the record itself untouched (`MarkFailedAsync` only runs for a first reading, never a
correction). The window this needs (extraction failing to finish within ~40 seconds of a correction
arriving) is narrow — reachable only via the same race N-4 traced.

**A URL-only reply to a non-receipt transaction silently becomes a plain re-read, with no
"send the link separately" notice.** Found during the P6-URL branch's Fable 5.1 review, not built
(minor, backlog). `CorrectionHandler.HandleEditAsync` checks the target already has a fiscal receipt
(`IReceiptStore.GetVerificationUrlAsync`) before deciding a pasted link is a *different* receipt and
sending `NewReceiptLinkMustBeSentSeparately` — but `TryHandleReplyAsync` (a Telegram *reply*, as
opposed to an edit) has no such check. A reply whose text is nothing but a verification URL, to a
transaction with no receipt row at all (an ordinary text/voice capture, or a failed link capture),
still becomes a `Correct` job; `CategorizationWorker.CorrectionFor` strips the URL via
`FiscalVerificationUrl.StripUrl`, gets back `null` (the "empty means none" contract), and the job
re-runs the categoriser with `Correction = null` — an unannounced re-read, not the "here's a receipt"
the person meant. Not built because it needs a design decision this branch's scope did not cover:
whether a URL-only reply to a receipt-less transaction should get its own notice text (distinct from
`NewReceiptLinkMustBeSentSeparately`, which talks about a *second* receipt), silently start extracting
the link as this transaction's own receipt, or something else — `TryHandleReplyAsync` does not know at
that point whether the target has a receipt, so wiring in a check is straightforward once the wording
is decided.

**The defer branch relies on `EfJobQueue`'s claim ordering, not an explicit dependency.** The same
defer mechanism only works because `ClaimAsync` already refuses to claim a job while an earlier job for
the same transaction is `Pending`/`Claimed` — a correction that arrives mid-extraction is therefore
almost never actually claimed in the deferred state at all; it waits behind the still-running
`ExtractReceipt` job instead. The deferral code exists for the narrow window between that job failing
and its transaction status actually flipping away from `Captured`. This is documented behaviour, not
untested — `CategorizationWorkerTests` covers the deferred branch directly — but the *reason* it is
rarely exercised is an ordering guarantee owned by a different class, worth knowing before either one
changes independently.

**Items carried from `final-rereview-2.md`, all now fixed:**

- **R2-2 (minor), fixed.** The N-2 refusal (an edited link capture that would re-file as a different
  receipt) used to overwrite the transaction's own echo with a bare refusal notice, dropping the
  Cancel/Edit buttons and the visible summary until a later reply corrected the record.
  `CorrectionHandler.HandleEditAsync` now posts the refusal through `chatNotifier.SendAsync` as its own
  message; the echo is never touched.
- **R2-4 (minor), fixed.** After a Cancel and Restore, a non-money receipt (Copy/Training/Proforma/Advance)
  landing `Captured` with no job pending used to render the generic "Recording…" acknowledgement forever.
  `RecordEcho.ComposeReceipt`'s `Captured` branch now checks `receipt.Kind.IsNonMoneyKind()`: a non-money
  slip renders `ComposeReceiptNotRecorded(receipt.Kind)` (with `[Edit]`); an ordinary receipt still being
  extracted renders `ReadingReceipt` instead.
- **R2-5 (minor, pre-existing), fixed.** An edited photo *caption* used to be dropped silently — Telegram
  delivers a caption edit as `Caption`, not `Text`, and `CorrectionHandler.HandleEditAsync` returned before
  finding the transaction at all. It now falls back to `Caption` when `Text` is absent and the edited
  message is a photo or document; a voice note's own caption is still ignored (its correction text is its
  transcript, not a caption).
- **R2-6 (documentation, low stakes), fixed.** The comment in `tests/Noof.Ledger.TestKit/DatabaseSettings.cs`
  now says plainly that the missing `CommandTimeout` is "the most likely explanation" for the flaky Npgsql
  read timeouts, not a traced root cause — matching the closed E2E-teardown-flake entry above, which
  carries the same caveat.

**Cancel/Restore on a receipt loses the receipt-specific echo styling.** `RecordActionHandler` renders a
Cancel/Restore through the generic `IRecordEcho.Compose`, not `ComposeReceipt` — the shop, location and
warning lines are gone after a Cancel/Restore round trip, though the record, its lines, wallet and
balance are all still correct underneath. Documented rather than fixed (Task 5's own call): making
Cancel/Restore receipt-aware needs an `IReceiptStore` lookup on every Cancel/Restore for every
transaction kind, for a purely cosmetic loss.

**A corrected receipt transaction's line items lose their link back to `receipt_lines`.**
`EfCategorizationStore.ApplyAsync`'s existing delete-and-replace for a `Correct`/`Reinterpret` job
removes every model-authored line regardless of whether it carries a `ReceiptLineId`, and the
replacement lines a text correction builds never set one. The `receipts`/`receipt_lines` rows themselves
are untouched — only the `line_items` ↔ `receipt_lines` link and the original receipt ordering are lost
on a corrected line. Documented, not fixed, by the same task that built the link.

**A separate, stronger model for `read_receipt`, and choosing a model in the UI — deferred by the
operator, 2026-09-27.** `read_receipt` stays on Haiku for now, same as every other tool call. Evidence
this was considered rather than overlooked: production reads on 2026-09-27 (before the "do not invent"
prompt and schema fix that day, `.claude/rules/receipts.md`) showed Haiku fabricating whole receipts from
low-quality photos — a real 4-line 1570.96 RSD cash receipt read back as "BISIBONSKA ŠTAMPA 110 RSD, 1
line", then as "МИНИСТЕРЕЛНИ 110 RSD" with an invented PIB, another as "MAXI HOLDING 2322 RSD, 16 lines"
whose lines summed to 7664, and one that took the capture's own location line for the PIB and the
capture time for the issue time. The `readable`/`unreadable_reason` fix and the "Record anyway"
confirmation flow (both 2026-09-27) make Haiku's mistakes visible and non-destructive rather than
requiring a stronger model outright; a model that reads more receipts correctly on the first try, or
letting the operator pick a model per call from Settings the way `IChatClientFactory` already permits at
the wiring level, is future work if the confirmation rate turns out to be high in real use.

**`SkiaReceiptImageScaler` corrects only the three rotation-only EXIF origins** (`BottomRight` = 180deg,
`RightTop`/`LeftBottom` = 90deg) that a phone camera or a scanner's own upright pass produce. The four
mirrored origins (`TopRight`, `BottomLeft`, `LeftTop`, `RightBottom`) — a horizontally- or
vertically-flipped scan, not a phone photo — are left untransformed. Not observed in production; add
`SKCanvas.Scale` flips for these if a flipped receipt photo ever surfaces.

**A fiscal link in a photo's caption, and several links in one message** (operator, 2026-09-27;
deferred). Today a photo whose caption carries a fiscal link is captured by its photo alone: the link
is ignored, and `CapturedReceipt`/`ck_transactions_capture_has_content` allow exactly one source
(`AddReceiptCaptureSourceXor`). Real fiscal QRs did not decode from any of the operator's photos
(`docs/OPEN-QUESTIONS.md`, Phase 6 QR entry), so that photo nearly always goes through the vision
fallback while an exact link was right there.
- **Operator's preference: the link wins when present.** Capture it as a link, since that gives exact
  Tax Administration data. Still to decide when building it: keep the photo's file id as a
  vision-fallback source for when the site is down (that relaxes the XOR constraint and needs a new
  migration), or drop the photo (then a down site gives the link capture's usual "send a photo instead"
  reply).
- **Several links in one message or caption** are probably several receipts and should become separate
  records, one per link, rather than only the first. Today `IFiscalVerificationUrl.TryFind` takes the
  first link and `StripUrl` removes all of them from the prompt text.

**`TelegramVoiceFileSource` has no size cap (found during the p6-dlcap fix round, 2026-09-27).**
`TelegramReceiptPhotoSource` refuses a photo over 10 MB, both from `GetFile`'s reported size and from
the bytes actually received during download (`SizeLimitedBuffer`), because a Telegram-reported size can
be missing, stale or simply wrong. The voice path (`GetInfoAndDownloadFile` into an unbounded
`MemoryStream`) has never had an equivalent check — confirmed absent from the start (`git log -S
MaxBytes` on it is empty), not a regression. Worth the same guard once a voice note has actually been
seen large enough to matter; no such case has shown up yet.

**Copilot findings deferred at the PR #3 close** (operator, 2026-09-27: after round 7, only security
bugs are fixed in this PR; the rest is recorded here).
- **The trace page's summary and receipt section order a receipt's lines differently.** The summary
  query in `EfTransactionTrace` (~:117) orders line items by `li.Id`, a random GUID, while the receipt
  section orders by `ReceiptLine.Ordinal`. The summary should order by `li.Ordinal` too, with a
  multi-line regression case, so both follow the receipt-order contract.
- **`TestHostLoggingDeleteTests` assumes Windows file-sharing semantics.** On a Unix runner an open
  file can still be unlinked, so the "directory remains while held" assertion fails. Harmless today
  because the app and its tests run only on Windows. Make that assertion conditional on
  `OperatingSystem.IsWindows()`, keeping the final-cleanup assertion on every OS, if the suite ever
  runs on Linux or CI.
