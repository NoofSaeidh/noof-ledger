# Open questions — deferred decisions

Parked from the approved design (`docs/superpowers/specs/2026-09-19-noof-finance-design.md`).
The spec was approved without answering these, so **each has taken its stated default**. None blocks the current phase. Each names the phase where it becomes real — revisit it there, or earlier if you want to change the default.

| # | Question | Default taken | Decide by |
|---|---|---|---|
| Q1 | Postgres credential: Windows-integrated auth (SSPI) or a DPAPI-protected password file? | **Neither** — a generated password in a plaintext file outside the repo | Phase 1 |
| Q3 | Write `%UserProfile%\.wslconfig`? WSL is running uncapped — 50% of RAM (~32 GB) + ~16 GB swap, and `ext4.vhdx` grows but never shrinks | **Not written** | Any time — unrelated to this project now containers are out |
| Q4 | Canonical mid-rate source for RSD: `open.er-api.com` for all five currencies, or add NBS *srednji kurs* for RSD? | **`open.er-api.com`** for all five | Phase 5 |
| Q5 | Accept the larger Phase 1, or split it and accept rework? | **Larger Phase 1** — build the capture-path contracts once | Settled by approval |
| Q6 | Move voice and receipts earlier than Phase 4? | **Keep at Phase 4** | Phase 2 |
| Q7 | Send the top ~50 canonical merchant names as a prompt hint? Best single lever for canonicalisation consistency, but puts a slice of the shopping profile in each request | **No** | Phase 4 |
| Q8 | OneDrive backup: dump only, or dump + Data Protection key ring? | **Dump only** — a restore means re-entering two secrets | Phase 8 |

## Why each default is safe to defer

**Q1** — SSPI was never spiked. What shipped instead is a generated password written to a plaintext file at `%LOCALAPPDATA%\NoofLedger\db.connection`, created by `ops/reset-database-auth.ps1` — outside the repo and never in `appsettings.json`. That is acceptable for a single-user local dev machine, but it is neither of the two options on the table, so the SSPI/DPAPI upgrade is re-parked here, decide by Phase 1.

**Q3** — Only matters while WSL runs. Nothing in the design touches it now.

**Q4** — The two providers disagree on RSD by **0.09%** while the spread being measured is 0.5–0.6%, so provider choice is roughly a sixth of the signal. `Source` is stamped on every rate row and the archive is append-only, so adding NBS later is a new source, not a migration.

**Q5** — Settled by approving the spec.

**Q6** — Dependency-driven: receipts need per-item categorisation (P1) and wallets/currencies (P2). Moving them earlier means building them twice.

**Q7** — Reversible at zero cost; it is one prompt field. Worth revisiting once real canonicalisation quality is visible.

**Q8** — Phase 8 by your own instruction. The real fork is whether a restore should be complete (key ring in the cloud) or safe (re-enter two secrets). A third option exists: `ProtectKeysWithCertificate` with the PFX in a password manager.

## Answered, kept for the record

| Question | Answer |
|---|---|
| Database engine | **PostgreSQL 18** — installed via choco |
| Project name | **Ledger** — `Noof.Ledger.*`, solution `NoofLedger.slnx`, database `noof_ledger` |
| `Money` ordering across currencies | **Throws.** Comparing 10 EUR to 10 USD has no correct answer. Callers sort mixed lists explicitly: `OrderBy(m => m.Currency).ThenBy(m => m.Amount)` |
| `CurrencyCode` ordering | **Ordinal, never cultural** — `string.CompareOrdinal`. Serbian Latin treats `LJ` as one collating letter and would invert pairs |
| `Microsoft.AspNetCore.Components.Authorization` version | **10.0.8**, lockstep with `Components.Web`. Verified to restore and build clean under CPM + `TreatWarningsAsErrors` |
| Containers (Docker/Podman) | **No**, for now — reasoning in §4 of the design |
| Money representation | `decimal` + `Currency`, native `numeric(19,4)` |
| Base currency | **EUR** |
| LLM | `claude-haiku-4-5` only, no adviser tier |
| Bilingual category names | `NameEn` + `NameRu` |

---

## Auth addendum questions

From §15 of the design, added 2026-09-19. All defaulted; none blocks any phase.

| # | Question | Default taken | Decide by |
|---|---|---|---|
| A1 | Does anyone else have a Windows account on this PC? | **No** | Any time — a yes means flipping `Auth:Mode=Cookie` immediately, since that is the single scenario where auth is load-bearing today |
| A2 | Will you reach the dashboard from your phone, and when? | **Not yet** | Enforced automatically: the startup guard refuses to boot on a non-loopback binding while `Auth:Mode=Off` |
| A3 | Re-prompt for the password on `/settings/secrets` after ~10 minutes? | **Policy attached, handler lenient** | Whenever auth is switched on |
| A4 | Set up trusted local HTTPS once, to unlock Windows Hello passkeys? | **No** — password stays the permanent mechanism | Any time |

**Why A2 needs no discipline from you:** the guard makes the config key and the Kestrel binding physically inseparable. The day you widen the binding for phone access, the app will not start until auth is on. That is what stops "optional now" from becoming "forgotten forever".

---

## Phase 0b questions — raised by the auth/data research

| # | Question | Default taken | Decide by |
|---|---|---|---|
| B1 | Does `user set-password` CREATE the `app_user` row, or require it to exist? There is no `/register` and no `/setup` page, so nothing else can create it | **Upsert** — it creates the row if absent. Otherwise the app has no path to a first user at all | Phase 0b, task 13 |
| B2 | Which entities beyond `AppUser` ship before Phase 1? | **None.** `MoneyProbeEntity` stays as the Money-mapping regression fixture and is dropped in Phase 1 when a real Money-bearing entity exists | Phase 1 |
| B3 | Should the loopback guard also reject a non-loopback *configured* URL pre-bind, as an early check? | **No** — the post-bind `IServerAddressesFeature` check is authoritative. Re-deriving Kestrel's URL precedence by hand is a bug source | Phase 0b, task 9 |

**Note on A1/A2.** `Auth:Mode` still defaults to `Off`, so the login screen is not in the way.
The startup guard makes the config key and the Kestrel binding inseparable: widening the binding
for phone access will refuse to start until auth is on. That is what stops "optional now" from
becoming "forgotten forever" — no discipline required from you.

---

## Phase 1 decisions — taken 2026-09-21

Four questions were put to the user before Phase 1 planning. All four are answered; two of them
changed the design that the readiness audit had recommended.

| # | Question | **Decision** |
|---|---|---|
| P1-1 | Category taxonomy — there was none anywhere in the repo or the spec | **Dynamic hierarchy.** Guid keys, self-referencing parent, sub-categories, bilingual `NameEn`/`NameRu`, renameable |
| P1-2 | Q1, the PostgreSQL credential (was due this phase) | **Leave as is.** Re-parked to a new hardening phase, by explicit instruction |
| P1-3 | Timezone for "today" / "this month" | **Per-transaction.** The zone is stored on the row, not assumed globally |
| P1-4 | How much parsing grammar Phase 1 owns | **None. Extraction is the LLM's job** |

### P1-1 — categories are data, not an enum

Guid primary keys, `ParentId` for sub-categories, `NameEn` + `NameRu`, and renaming must not break
anything. That last requirement is the one with teeth: **a renameable display name cannot be the key
the model answers with.** Every category therefore carries an immutable `Slug` that is minted once and
never changes; the model's structured-output enum is built from current slugs at call time, and
`category.NameRu` can be rewritten freely without invalidating a single stored categorisation.

Consequence for the request schema: the enum is per-request data, not a compile-time constant. There
is no `CategoryKind` enum in the codebase and there must not be one.

Seed: a starting tree, not a fixed taxonomy — the user renames and extends from there. A management
UI is NOT a Phase 1 deliverable; the schema supports renaming from the first migration, which is the
half that is expensive to retrofit.

### P1-2 — Q1 is deferred, deliberately, and a new phase is owed

The user's words: *"Оставь как есть до последней фазы (нужны новая фаза почистить и подготовить к
'продакшну' руками)."* So Q1 does not move in Phase 1, and a **hardening / production-readiness
phase** is now owed at the end of the roadmap. It should collect at least: the DPAPI-or-SSPI
credential decision, a real look at what is logged, and whatever else accumulates as "fine for a
single-user dev machine".

Separately, and NOT part of that deferral: `ops/reset-database-auth.ps1:76` writes
`Include Error Detail=true` into the credential file, so it reaches the application's runtime
connection string and puts **parameter values into Npgsql exception text**. That is a secrets-leak
surface and Phase 1 is when secrets start flowing through EF. Strip it from the runtime string in
Phase 1; it is unrelated to how the password is stored.

### P1-3 — the zone lives on the row

Storage stays `DateTimeOffset` → `timestamptz`; that was already settled. What is new is that
bucketing into a local day must not depend on the machine's clock. Telegram does not report the
sender's zone, so the row's zone is stamped at capture time from a current-zone setting (default
`Europe/Belgrade`) that the user can change when they travel. History then stays honest: a spend made
in Belgrade keeps its Belgrade day even after the setting moves.

`time_zone text` (IANA id) on the transaction, in the first migration. Retrofitting it means
re-bucketing history against an assumption nobody wrote down.

### P1-4 — extraction is the model's job, and what that costs

The user's words: *"Это должно быть на стороне ллм только."* This overrides the readiness audit's
central recommendation, which was a deterministic parser in Domain. There is no hand-rolled grammar,
no currency-alias table, and no tokenizer in Phase 1.

**Two things in the approved spec do not survive this unchanged, and are recorded here rather than
quietly dropped:**

1. Spec:157 requires the immediate Telegram reply to carry the total and running balance as
   **model-free numbers**, before any model call. If nothing extracts the amount offline, that reply
   cannot contain an amount. **Resolution:** with the network down the bot saves the raw message and
   replies that it is saved and will be processed; the amount and balance appear when the message is
   edited after processing. The acceptance criterion's "still saves" holds — what is saved is the raw
   text, and nothing is ever lost.
2. CLAUDE.md is absolute that no user-facing number originates from a model. **Resolution:
   quote-and-verify, which the spec already describes at :159.** The model returns the *substring* it
   believes is the amount; C# asserts that substring occurs verbatim in the stored `raw_text` and then
   parses it itself with `decimal.Parse`. The number is therefore computed by C# from verified input,
   never transcribed from a model's arithmetic. A model that paraphrases instead of quoting fails the
   check and the job goes to the failed state rather than inventing a figure.

Currency is simpler than the audit assumed: the model returns an ISO code constrained by the
structured-output enum to the five supported codes, so `CurrencyCode` is constructed from ASCII and the
`рсд` blocker below never arises.

> **The blocker that made this decision necessary, kept for the record.** `CurrencyCode.cs:7` rejects
> any value failing `char.IsAsciiLetter`. The literal token in the acceptance test, `рсд`, is three
> characters — so the length check passes — and then throws on the ASCII check. Verified directly.
> `CurrencyCode` must NOT be loosened: its ASCII/ordinal invariant is a settled decision driven by the
> Serbian `LJ` collation bug. Any future deterministic parser must map aliases outside the type.

---

## P1-5 — Telegram drops unfetched messages after 24 hours, and a bot cannot read history

**Not a decision. A verified constraint of the platform**, recorded because the design's own framing
("the PC was off") runs straight into it and nothing said so.

Checked against `https://core.telegram.org/bots/api` on 2026-09-21, not from memory:

> Incoming updates are stored on the server until the bot receives them either way, but they will not
> be kept longer than **24 hours**.

And the more consequential half: **the Bot API has no method for fetching past messages.** There is no
`getChatHistory`, no `getMessages`, nothing equivalent. A bot receives messages only going forward, via
`getUpdates` or a webhook. So "it is just a chat, the service reads the new messages each time it runs"
does not hold — not because of how this is implemented, but because bots cannot look backwards at all.

**What it costs, concretely.** The spec names "your PC was off" as the common case. Off for under a day:
nothing is lost, the poller drains the backlog on the next start. Off for longer — a weekend away, a
holiday — and every message sent outside the last 24 hours is **gone**, with no way to recover it. The
same window is what makes a permanently stalled poller a data-loss bug rather than an availability one,
which is why `TelegramPollingService` now gives up on a poisoned update after three attempts, tells the
operator in the chat, and moves on.

**The only thing that would change this** is a client on MTProto under the operator's own Telegram
account rather than a bot token: a user client *can* read history, so a service could reconcile whatever
it missed on startup. The price is that it holds full account credentials instead of a token that can
only write to one chat — a materially different secret to be storing, in a project whose repository is
public. **Undecided, and deliberately not decided here.** Revisit if the "off for a week" case turns out
to matter in practice; until then the honest statement is the one in the README, not "nothing is lost".

---

## P1-6 — the capture relay: the cloud receives, the PC drains

**Decided 2026-09-21**, after two research passes (7 areas then 6, each independently verified; the
second flagged **28 quoted prices that their own citations did not support**, which is the reason nothing
below is repeated from memory).

### The decision

A Telegram **webhook** posts each update to a small always-on cloud function, which appends it to a
queue. The local application no longer long-polls Telegram at all; it **drains that queue outbound over
HTTPS** whenever it is running, and acknowledges what it has stored.

Everything else stays exactly where it is: the Blazor dashboard, PostgreSQL, the job worker, the
DPAPI-protected key ring, the loopback interlock, and the plaintext local database credential. **No
ledger data, no bot token and no key material leaves the machine.**

**Rejected: the MTProto userbot.** The operator's words: *"не хочу в юзерботов с такими рисками идти."*
The cost objection had collapsed — a Serbian prepaid SIM is about €12 one-off, not the ~$2,842 that
Fragment's resale market now asks — but the risk objection did not. A fresh account on a new number
running an unofficial client is the profile in every documented ban report, while an established account
is safer and is the personal identity the operator specifically did not want to stake. No authoritative
ban statistic exists for a passive reader, and none is going to.

**Rejected: hosting the whole application.** €6/month forever, a 2–4 day Linux port, and — the part that
decides it — `ProtectKeysWithDpapi()` is Windows-only and a certificate-protected key ring cannot decrypt
what DPAPI wrote, so **every stored secret would have to be re-entered**. It also puts a public-repo
finance application on the open internet for no gain the relay does not already provide.

**Not needed: edge encryption.** The operator was asked directly whether raw expense text may sit with a
provider and answered that it is not a secret. So the relay stores plaintext updates and the ECDH
scheme the research proposed is dropped — half a day saved on a protection nobody wanted.

### What this honestly does and does not buy

**It does not close the 24-hour window. It removes the PC's uptime from the equation.**

The same buffer applies to webhook mode — *"Incoming updates are stored on the server until the bot
receives them either way, but they will not be kept longer than 24 hours"* — and Telegram retries a
failing webhook *"a reasonable amount of attempts"*, a budget documented nowhere
([core.telegram.org/bots/api](https://core.telegram.org/bots/api), read 2026-09-21).

So the residual risk becomes: the cloud function must answer 2XX within an unknown retry budget, and at
worst within 24 hours. A managed function's availability is in a different class from a desktop that is
deliberately switched off for a weekend — which is the actual problem — but this is a very large
reduction, not a guarantee. Only reading history would have been a guarantee, and that route was
rejected on its own terms. **Say "the PC being off no longer loses anything", never "nothing is lost".**

### Shape

- **AWS Lambda Function URL** + **DynamoDB** in provisioned-capacity mode. Verified free allowances
  (Lambda 1M requests and 400k GB-seconds per month; DynamoDB 25 WCU/25 RCU/25 GB) sit orders of
  magnitude above roughly twenty messages a day, so the expected bill is **$0.00**.
- `.NET 10` is a GA managed Lambda runtime (`dotnet10`, Amazon Linux 2023), so the relay is C# like
  everything else. Azure was considered and is workable on Flex Consumption, but Microsoft's own pages
  contradict each other on .NET 10 support while AWS's do not.
- `setWebhook` carries a **`secret_token`**, and the function rejects any request whose
  `X-Telegram-Bot-Api-Secret-Token` header does not match. That is what stops anyone who finds the URL
  from injecting expenses.
- Webhook and `getUpdates` are **mutually exclusive**, so this replaces the Telegram-facing half of
  `TelegramPollingService` rather than adding to it.
- Dedup stays on `update_id`, and the existing `(chat_id, message_id)` unique index keeps working
  unchanged — the message ids are still Bot API ids, which is precisely why the userbot route would have
  broken it.

### The one assumption nobody could verify from documentation

**Whether Telegram accepts the TLS certificate of a `*.lambda-url.<region>.on.aws` hostname.** No primary
AWS or Telegram page states it. It is the single biggest unknown under this plan, it is settled by a
two-hour spike with a throwaway bot, and **if it fails the plan changes shape** (an API Gateway custom
domain, or Azure, would be the fallback). Do that spike before writing anything else.

Second unknown, and it interacts with the first: **cold-start latency** of `dotnet10` for a function
invoked a few dozen times a day. Because the retry budget is undocumented, a slow cold start is a
correctness question rather than a latency curiosity. SnapStart is available on `dotnet10` if needed.

### P1-6 addendum — two follow-up decisions, 2026-09-21

**Why AWS rather than Azure**, since the $0 holds on both and the cost is not the reason:

1. **Function URL removes a trap.** A research brief recommended Lambda *plus API Gateway* and asserted
   $0, citing only Lambda's pricing. API Gateway's HTTP API free tier is **12 months**, not permanent, so
   that architecture would have started billing in year two. A Function URL puts no gateway in the path.
2. **AWS's .NET 10 story has no contradiction.** `dotnet10` is a GA managed runtime with a published
   deprecation date. Microsoft's own pages disagree with each other — the functions-versions table marks
   .NET 10 GA while the Visual Studio section on the same page still calls it preview — and .NET 10 will
   not run on the classic Linux Consumption plan at all, only on Flex Consumption.
3. **The free grants are unambiguous on AWS.** Lambda 1M requests + 400k GB-seconds, DynamoDB 25 WCU /
   25 RCU / 25 GB in provisioned mode, both permanent. Flex Consumption's grant is **250k executions +
   100k GB-seconds** — a quarter of the figure two briefs quoted, because they cited the *classic*
   Consumption grant that the required plan does not use. Azure's overage rates render as `$-`
   placeholders and could not be established at all.

One reason was **discarded** rather than kept: the comparison of DynamoDB's per-item expiry against Cosmos
Table API's table-level expiry is irrelevant when everything shares one 90-day lifetime. Recorded so it is
not resurrected as justification later.

**Whether the cloud function sends the "saved" acknowledgement: deferred to after the spike.**

In the v1 shape the function only receives and stores, so **the bot token never leaves the PC** and the
settled secrets rule survives untouched; what sits in AWS is two random strings that decrypt nothing. The
price is that the acknowledgement arrives late — when the machine returns and drains — instead of in
seconds. Making it instant requires putting the bot token in AWS, which is a real secret leaving the
encrypted store.

The operator chose to build v1 without it, see how the delay feels in practice, and decide then. Build
accordingly: the relay must not be structured so that adding a cloud-side reply later means reshaping it.

**Order of work: Phase 1B first, the relay after.** Verified that this creates no rework — Phase 1B lives
downstream of capture, in the queue and the worker, and barely touches `TelegramPollingService`, which is
the class the relay rewrites. The relay also cannot start until there is an AWS account and a throwaway
bot to spike against, and Phase 1B has no such dependency. The deciding reason is neither: until Phase 1B
exists, a captured message never becomes a categorised expense, so there is very little to lose by being
away.

### P1-6 SUPERSEDED — 2026-09-21. Nothing is built. The application stays local.

The relay described above is **not being built**. Neither is hosting, and neither is the userbot. The
operator's decision after the price re-check: *"оставляем пока только локально."*

The reasoning above is kept because it is correct and was expensive to establish — but its conclusion
was overturned by one fact the first survey missed, found only when the prices were challenged:

> **A tunnel (Cloudflare Tunnel or Tailscale) gives phone access to the dashboard with the application
> staying at home, for nothing, with no inbound port open.**

That collapses the case for hosting. Hosting was worth considering because it bought three things at
once — always-on capture, remote dashboard access, and no third-party queue. Remote access is now free
and separable. Always-on capture is addressed more cheaply by waking the machine on a schedule. And the
third-party queue is a cost of the relay, not a benefit of hosting.

So none of the three remaining options is worth its price today:

| Option | Why not now |
|---|---|
| Relay (Lambda + DynamoDB, $0) | 2–3 days, the project's first external dependency, raw expense text in AWS — to solve a problem a free OS setting may already solve |
| Whole-app hosting (€5–6/mo) | Every stored secret re-entered (DPAPI does not travel), a 2–4 day Linux port, and a public-repo finance app on the open internet |
| MTProto userbot | Rejected on its own terms — see the decision above |

**What remains true and unchanged:** the 24-hour limitation recorded in **P1-5** is still live. A machine
off for longer than a day still loses the messages sent in that window. That is now an accepted,
documented limitation rather than a problem being solved — and the mitigations are in `docs/BACKLOG.md`,
ready to pick up when the product is actually in daily use and the gap is felt rather than imagined.

**Order of work is unaffected:** Phase 1B was always first, and remains so.
