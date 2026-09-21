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

---

## `UseForwardedHeaders` is missing, and `CookieSecurePolicy.SameAsRequest` depends on it

**Wanted.** `app.UseForwardedHeaders(...)` configured before authentication, so the app sees the original
scheme when something terminates TLS in front of it.

**Why it is not scheduled.** Not reachable today: the app binds loopback only, nothing sits in front of
it, and `LoopbackGuard` refuses a non-loopback bind unless cookie auth is on. The capture relay decided
in `OPEN-QUESTIONS.md` P1-6 does not change that — the drain is outbound, so nothing proxies inbound.

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

## Two lessons about researching prices, kept deliberately

**Vendor marketing pages frequently do not render prices to a fetch.** Azure's shows `$-` thirty-six
times; Hetzner's homepage widget shows none at all; Oracle's spec table is injected by JavaScript. This
is exactly how wrong numbers get quoted from memory and then cited to a page that does not contain them.
Azure's real prices are in the **retail prices API** (`prices.azure.com/api/retail/prices`) as plain JSON.

**Check whether a free tier is permanent or a 12-month trial.** That distinction flipped one
recommendation entirely, and it is invisible in every comparison article.
