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

**Do not** reach for the EF InMemory provider. `CLAUDE.md` bans it for good reasons, and every one of
these tests exists precisely because it runs against real PostgreSQL.

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

Both databases were dropped and recreated from migrations, and `CLAUDE.md` now carries the rule that
caused it. What is still missing is a guard: nothing compares the template against a freshly
migrated database, so the next drift will be found the same way — by luck. A test that migrates a
scratch database and diffs `pg_dump --schema-only` against the template would close it, at the cost
of one full migration run per suite execution. Phase 2 added the runbook step "After adding a
migration" and a CLAUDE.md rule; the drift guard itself is still missing.

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

## Line items keep no order

`line_items` has no ordinal column, so the echo and the snapshot list lines by description rather than
in the order the message named them. Harmless for one- and two-line messages; a receipt (Phase 6) will
want its own order, and that is a column plus a migration.

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
reserved in the enum but not implemented — a transfer becomes two entries (one per wallet) with no
schema change needed when that phase arrives. Moving cash between wallets today is two separate
manual transactions (an expense from one, an income to the other), which loses the "this was the same
money" relationship a real transfer would keep.

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

**A separate PostgreSQL instance for tests** (own port, fsync off, no real data on it) — faster
database tests, no shared lock between worktrees, and test clones never on the server that holds
`noof_ledger`; deferred by the operator on 2026-09-24 as a follow-up after Phase 4.
