# Open questions — deferred decisions

Parked from the approved design (`docs/superpowers/specs/2026-09-19-noof-finance-design.md`).
The spec was approved without answering these, so **each has taken its stated default**. None blocks the current phase. Each names the phase where it becomes real — revisit it there, or earlier if you want to change the default.

| # | Question | Default taken | Decide by |
|---|---|---|---|
| Q1 | Postgres credential: Windows-integrated auth (SSPI) or a DPAPI-protected password file? | **Neither** — a generated password in a plaintext file outside the repo | Phase 1 |
| Q3 | Write `%UserProfile%\.wslconfig`? WSL is running uncapped — 50% of RAM (~32 GB) + ~16 GB swap, and `ext4.vhdx` grows but never shrinks | **Not written** | Any time — unrelated to this project now containers are out |
| Q4 | Canonical mid-rate source for RSD: `open.er-api.com` for all five currencies, or add NBS *srednji kurs* for RSD? | **`open.er-api.com`** for all five | Phase 7 (was 5) |
| Q5 | Accept the larger Phase 1, or split it and accept rework? | **Larger Phase 1** — build the capture-path contracts once | Settled by approval |
| Q6 | Move voice and receipts earlier than Phase 4? | ✅ **Answered 2026-09-22: voice moves to Phase 3**, right after natural-language capture; receipts stay later (Phase 6) | Settled |
| Q7 | Send the top ~50 canonical merchant names as a prompt hint? Best single lever for canonicalisation consistency, but puts a slice of the shopping profile in each request | **No** | Phase 6 (was 4) |
| Q8 | OneDrive backup: dump only, or dump + Data Protection key ring? | **Dump only** — a restore means re-entering two secrets | Phase 10 (was 8) |

## Why each default is safe to defer

**Q1** — SSPI was never spiked. What shipped instead is a generated password written to a plaintext file at `%LOCALAPPDATA%\NoofLedger\db.connection`, created by `ops/reset-database-auth.ps1` — outside the repo and never in `appsettings.json`. That is acceptable for a single-user local dev machine, but it is neither of the two options on the table, so the SSPI/DPAPI upgrade is re-parked here, decide by Phase 1.

**Q3** — Only matters while WSL runs. Nothing in the design touches it now.

**Q4** — The two providers disagree on RSD by **0.09%** while the spread being measured is 0.5–0.6%, so provider choice is roughly a sixth of the signal. `Source` is stamped on every rate row and the archive is append-only, so adding NBS later is a new source, not a migration.

**Q5** — Settled by approving the spec.

**Q6** — Answered. Voice never depended on wallets — it rode along with receipts. It is the operator's main capture path, so it follows directly after natural-language capture (`docs/superpowers/specs/2026-09-22-natural-language-capture.md`). Receipts keep their dependency on the money model.

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

### A1/A2 SUPERSEDED — 2026-09-22. `Auth:Mode=Off` is deleted; cookie authentication is the only mode.

Found while auditing the two-mode setup: `/settings/secrets` disables prerendering and declares
`[Authorize]`, and `Routes.razor`'s `AuthorizeRouteView` has no `<NotAuthorized>` content, so under
Off mode — where every request authenticates automatically with no credential check — the page
rendered **blank** rather than showing anything, authorized or not. Nobody had caught it because
every E2E page test ran in Cookie mode and the only Off-mode fixture pointed at a deliberately dead
database.

The operator's instruction was not to fix Off mode but to delete it: *"Проще - лучше."* Cookie
authentication is now the sole scheme `Program.cs` registers, so A1 and A2's defaults no longer
select between two real code paths — there is only one. The loopback interlock (`LoopbackGuard`,
A2's enforcement mechanism) is unaffected in spirit and strengthened in fact: it no longer reads an
auth mode at all and refuses any non-loopback binding unconditionally, which is what "the config key
and the Kestrel binding are physically inseparable" now means literally rather than only while a
particular mode is selected. The rows above and the note below are kept for the record; they were
correct descriptions of the two-mode design while it existed.

---

## Phase 0b questions — raised by the auth/data research

| # | Question | Default taken | Decide by |
|---|---|---|---|
| B1 | Does `user set-password` CREATE the `app_user` row, or require it to exist? There is no `/register` and no `/setup` page, so nothing else can create it | **Upsert** — it creates the row if absent. Otherwise the app has no path to a first user at all | Phase 0b, task 13 |
| B2 | Which entities beyond `AppUser` ship before Phase 1? | **None.** `MoneyProbeEntity` stays as the Money-mapping regression fixture and is dropped in Phase 1 when a real Money-bearing entity exists | Phase 1 |
| B3 | Should the loopback guard also reject a non-loopback *configured* URL pre-bind, as an early check? | **No** — the post-bind `IServerAddressesFeature` check is authoritative. Re-deriving Kestrel's URL precedence by hand is a bug source | Phase 0b, task 9 |

**Note on A1/A2 — SUPERSEDED 2026-09-22.** This paragraph described `Auth:Mode` defaulting to `Off`
so the login screen stayed out of the way. That key no longer exists: cookie authentication is the
only mode, and the loopback interlock now refuses a non-loopback bind unconditionally rather than
only while auth was off. Kept for the record of why the toggle seemed worth having.

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
2. *(Superseded 2026-09-22 by P2-1 below.)* CLAUDE.md is absolute that no user-facing number originates from a model. **Resolution:
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

---

## Phase 2 decisions — taken 2026-09-22

### P2-1 — quote-and-verify is removed; the safety is undo, not rejection

The operator's words: *"не надо делать валидацию. просто в чат тг должен присылаться распознанный
вариант. и его можно уже руками поправить. или написать как обработать. но главное его можно
отменить и изменить. это важнее guarda"*.

Quote-and-verify (P1-4 point 2) rejected every amount not written as digits — *"штуку евро"*,
*"полтос"*, and most speech-to-text output. Voice is the main capture path, so the guard blocked the
main use case. The model now interprets amounts and dates freely; the bot echoes the stored result
with **Отменить** / **Изменить**; a reply in free text corrects it; every state is kept in
`transaction_revisions`. The CLAUDE.md money rule keeps its force for reports, totals and balances,
and no longer applies to capture. Full design:
`docs/superpowers/specs/2026-09-22-natural-language-capture.md`.

**Do not re-propose a validation layer on capture** — evidence spans, verbatim checks or sanity bounds
— without the operator asking. It was considered and refused in favour of cheap undo.

### P2-2 — a correction's "today" is the reply's own send day, not the original message's

The plan (line 1867) fed every job `Today = sub.SentOn`, the day the original message was captured.
A per-task review flagged that a Correct job needs the reply's own day instead, and the plan left it
deferred rather than deciding it — a gap, not a decision, caught by the closing review
(2026-09-23). Left as written, a purchase captured Monday corrected on Wednesday with *"это было
позавчера"* resolves позавчера from Monday, not Wednesday, and every relative word in a correction
is off by however long the correction waited.

**Resolution:** `categorization_jobs.instruction_day` (nullable `date`, migration `AddInstructionDay`)
stores the correction reply's own local day, computed by `EfRecordEditor.RequestCorrectionAsync` from
the reply's `sentAt` and the transaction's `time_zone_id`. `CategorizationWorker` reads
`job.InstructionDay ?? sub.SentOn` as `Today`, so a first reading and a Reinterpret job (no
`InstructionDay`) are unaffected and keep D2's rule; only a Correct job's anchor moves.

### P2-3 — a correction is a job, not a table *(taken 2026-09-22, Phase 2 plan)*

`categorization_jobs` gained `kind`, `instruction` and `source_message_id` instead of a separate
corrections table, because a correction needs exactly the claim/lease/retry/attempt-cap machinery the
queue already has. The cost is an ordering rule in `ClaimAsync`: a job is never claimed while an earlier
job for the same transaction is Pending or Claimed, or a correction could be applied and then
overwritten by the reading it corrected. A failed correction never marks a transaction Failed.

### P2-4 — the revision history's shape *(taken 2026-09-22, Phase 2 plan)*

One `transaction_revisions` row per state, with `status_before` and `status_after`, the instruction,
and a jsonb snapshot whose amounts are decimal strings. `status_before` is what Вернуть restores.
Append-only by trigger; the FK is RESTRICT, so a revised transaction can never be deleted.

### P2-5 — operator review of Phase 2 (2026-09-23)

The operator reviewed the finished phase and made six decisions, since implemented (see
`CLAUDE.md` §3/§4). Recorded here for the reasoning; the rules themselves live in `CLAUDE.md`.

| # | Decision | Reasoning |
|---|---|---|
| D-A | Nothing depends on the LLM provider except its `IChatClientFactory` implementation, confined to `src/Noof.Ledger.Ai/Anthropic/` (not a separate project) | A provider swap should touch one folder, not a project boundary that would force every provider-neutral type to move with it; `AiBoundaryTests` enforces it by scanning source text, not by convention |
| D-B | The tool loop runs through `Microsoft.Extensions.AI`'s `FunctionInvokingChatClient`, not a hand-written loop | The framework already implements the request/response/tool-result cycle correctly; a hand loop duplicated that logic with no behavioural difference to defend |
| D-C | Public services go through an interface, except simple helpers named individually | A substitutable seam needs a name other than its one implementation; `MerchantName.Fold` and `SecretKeys` are stateless helpers with nothing to substitute, so an interface there would be ceremony, not a seam |
| D-D | The bot writes English only for now, including the category name shown in the echo (`NameEn`) | Multi-language is real scope — resource strings, a language setting — that Phase 2 never budgeted; shipping one language now and widening later costs nothing today, and the operator may still write to the bot in any language |
| D-E | Identifiers and comments use English action names (`Cancel`/`Edit`/`Restore`), never the Russian UI labels | A Russian identifier reads as Russian-only code to anyone who does not read Russian; the UI text and the code's own names are different axes, and only one of them needed to change for D-D |
| D-F | Currencies outside the five known codes: deferred, docs only | The strict schema's enum is a compile-time set; widening it safely means splitting `CurrencyCode.Supported` into nameable vs. rateable and reopening Q4 (the rate-source question), which is Phase 4/7 work, not a Phase 2 fix — `docs/BACKLOG.md` |

**Why FICC needed a guard client (D-B).** `FunctionInvokingChatClient` resets a required `ToolMode`
after its first round and strips every tool declaration from the request it sends on its own last
iteration — verified by decompiling 10.5.1, not from its documentation. Left alone, the follow-up
request after a `list_merchants` answer would go out with every tool available again (or none), so
the model could ask for the merchant list a second time, or answer with plain text instead of
`record_spending` (renamed to `record_transaction` in Phase 4). `AnswerToolGuard`, a
`DelegatingChatClient` sitting below FICC, rewrites that one follow-up request to offer
`record_spending` (renamed to `record_transaction` in Phase 4) alone and force it — the answer
channel stays API-enforced rather than depending on the model behaving.

**Why the stored secret key string stayed `"anthropic-api-key"`.** D-A moved the constant out of
`Noof.Ledger.Application.Secrets.SecretKeys` and into `AnthropicChatClientFactory`, but the string
itself is unchanged, byte-for-byte: it is a durable database key, and every operator who has already
saved a key has a row under that exact string. Renaming it would silently orphan that row rather than
surface as an error. Provider-neutral naming was worth doing for the code; it was not worth a silent
secret-store migration.

### P3-1 — Speech-to-text provider (2026-09-23)

**Decision: Groq's hosted `whisper-large-v3`** — the operator's call, *"Давай грок"*, after a comparison of four options researched and adversarially fact-checked against vendor pages on 2026-09-23.

| Option | Why not (or why) |
|---|---|
| **Groq `whisper-large-v3`** (chosen) | Accepts Telegram's OGG/Opus as uploaded (name it `.ogg`; `.oga` is refused). Free tier without a card: 20 requests/min, 28,800 audio-seconds/day — ~60× this volume; paid ≈ $0.42/month. No training on customer data; not retained by default; zero data retention is a self-serve console switch. US-hosted; no SLA. |
| OpenAI `gpt-transcribe` | ≈ $1/month, no retention on the transcription endpoint, no training. Does not accept OGG — needs an Opus decoder in C#. Its older transcription models were deprecated 2026-08-26 (removal 2027-02-26). The operator's first preference, set aside because Groq needs neither the decoder nor a paid balance. |
| AWS (Transcribe + Claude on Bedrock) | Batch transcription requires S3 and polling; Transcribe may keep audio to improve its models unless an Organizations opt-out policy is set; strict tool use on Bedrock is undocumented and reported broken for Sonnet 5. |
| Azure (AI Speech / OpenAI + Claude in Foundry) | Fast transcription has no `ru-RU`; Claude in Foundry has no EU data zone; billing through Azure Marketplace. |
| Local Whisper on the operator's GPU | Private and free, but Vulkan on the RX 7900 XT under Windows was unverified, and a 1.6 GB model and native runtime to ship. Not chosen; the seam keeps it possible. |

**What keeps this cheap to reverse:** transcription goes through `ISpeechToTextClient` from a factory that only `src/Noof.Ledger.Ai/Groq/` implements (V2). OpenAI speaks the same wire protocol; moving there is a new folder, a key and a decoder. Real-voice comparison belongs to Phase 11 calibration.

### P3-2 — Forced tool use rules out some Claude models (found 2026-09-23)

AWS's Bedrock documentation states, under "Forced tool use"
(`docs.aws.amazon.com/bedrock/latest/userguide/model-parameters-anthropic-claude-messages-tool-use.html`):
*"Claude Opus 5.5, Claude Fable 5.1, and Claude Mythos 5.1 do not support forced tool use. A request
that sets `tool_choice` to `{"type": "any"}` or `{"type": "tool", "name": "..."}` returns a `400
invalid_request_error`."* Anthropic's own documentation confirms the same restriction independently,
not merely by Bedrock's word for it:
`platform.claude.com/docs/en/agents-and-tools/tool-use/implement-tool-use` ("Forcing tool use"
section) carries a table naming the identical three models — *"Claude Opus 5.5, Claude Fable 5.1, and
Claude Mythos 5.1"* — with the restriction *"`any` and `tool` return a 400 error"* and the recommended
replacement *"`auto` with strict tool use ... or structured outputs."* The app runs
`claude-haiku-4-5`, so nothing breaks today, but Phase 2's forced strict tool call (`tool_choice`
any/tool, `docs/superpowers/specs/2026-09-19-noof-finance-design.md` and `CLAUDE.md` §4) cannot run on
Claude Opus 5.5, Fable 5.1 or Mythos 5.1. Moving to one of them is a design change to the answer
contract — `auto` plus strict tools, or structured outputs — not a model-id change.

### P4-1 — Phase 4 money model decisions (2026-09-24)

The operator made these decisions during Phase 4 spec discussion; the rules themselves live in
`CLAUDE.md` and `docs/superpowers/specs/2026-09-24-money-model.md` (M1–M13, B1–B5). Recorded here for
the reasoning.

| # | Decision | Reasoning |
|---|---|---|
| M1 | A wallet's balance is derived from entries and checkpoints, never stored as a column | A cached balance drifts from what produced it the moment anything writes around it instead of through it; deriving it is the only way "exact" stays true after the code that computed it once is forgotten |
| M3/M4 | `transactions.wallet_id` becomes nullable; the wallet is chosen when the record is read (the model names it, or the currency's default), not at capture | Capture no longer needs a wallet at all, and "no default wallet" stops being a capture-time failure — it only matters once the model has actually named a currency |
| M6 | A balance statement is a checkpoint (`balance_checks`), not an adjustment entry | An adjustment entry would hide the operator's own correction inside the same ledger as ordinary spending; a checkpoint keeps it as what it is — a fact about reality overriding the app's own running total — and gives the dashboard a "last reconciled" date for free |
| M10 | Cross-currency spending is not converted; it is recorded in its own currency, shown as a separate balance line | *"курсы зависят от банка — отложи"* (operator). Inventing a rate the bank would not actually use is worse than an honest, visible second currency line — `docs/BACKLOG.md` records the deferred conversion work |
| B2 | An external process's secret goes through its environment only, never an argument | `Process.Start` arguments are visible to anything that can list processes (Task Manager, `Get-Process`, a crash dump); an environment variable set via `ProcessStartInfo.Environment` is not |
| B5 | `restore-check.ps1` runs against a real database only once, by the operator's own hand, with explicit permission | The script legitimately reads `noof_ledger` to compare against a restored scratch copy — never writes to or drops it — but "this script only ever reads the real ledger" is a claim worth the operator's own eyes once, not something Phase 4's automated tests should assert on his behalf against his real data |

### P5-1 — Phase 5 observability decisions (2026-09-24/25)

Taken during the observability spec conversation; full detail in
`docs/superpowers/specs/2026-09-24-observability-design.md`. Rules that bind future work live in
`CLAUDE.md`; reasoning is recorded here.

| # | Decision | Reasoning |
|---|---|---|
| O-1 | The host waits for PostgreSQL **indefinitely** and never exits on its own for a missing database | A single-operator local app has no orchestrator to restart it on exit; a host that gives up and dies waits for a human to notice and relaunch it, which is strictly worse than one that keeps retrying with backoff and answers `/healthz` throughout |
| O-2 | Log files live in a configurable folder (`Logging:File:Directory`), default `%LOCALAPPDATA%\NoofLedger\logs`; daily roll, 14 files, 50 MB cap each | Matches the existing backup retention shape (`%LOCALAPPDATA%\NoofLedger\backups`, newest 14) rather than inventing a second convention; configurable because the default drive may not be where the operator wants months of logs |
| O-3 | Serilog is only the `Microsoft.Extensions.Logging` provider; all code logs through `ILogger<T>` + `[LoggerMessage]`; Serilog packages are referenced by `Noof.Ledger.Host` only | Keeps the same provider-boundary shape as the AI factories (D-A) — nothing outside the composition root depends on which logging library is behind `ILogger<T>`, so swapping Serilog later touches one project; `[LoggerMessage]` is source-generated, so `CA1848`/`CA2254` can be errors with no runtime cost |
| O-4 | The database sink is the ready-made `Serilog.Sinks.Postgresql.Alternative`, not a custom sink; no separate connection isolation; the log table is an ordinary EF migration | A hand-written sink is a second thing to keep correct under load and shutdown; the original 2026-09-19 design's "separate connection, pre-migration DDL" was superseded because nothing about this app's scale needs the log path decoupled from the ledger's own migration and connection lifecycle |
| O-5 | The most important thing logged is the **transaction**: its path (Received ▸ Transcribed ▸ Categorized ▸ Persisted ▸ Replied) plus its revision history, on one page | The operator's actual question when something looks wrong is "what happened to this message", not "show me every log line" — a per-transaction trace answers that directly, and stable `EventId`s (5001–5005, 5009) keep the trace reader and the writers from drifting apart |
| O-6 | Health shows as a dashboard tile **and** on `/diagnostics` | The tile is what is seen without looking for it; the full page is for when something needs investigating — one seam (`ISystemHealth`) serves both so they can never disagree |
| O-7 | `/health` is a bot command, **owner-only**, and never claims ownership | The bot already has an owner-claim mechanism from Phase 2 (the first chat to write becomes the owner); reusing it for `/health` costs nothing and keeps system state out of any chat but the operator's own |

**What this closes.** P1-2's owed "hardening / production-readiness phase" asked, among other things,
for "a real look at what is logged" — Phase 5 is that look: structured logging replaces ad hoc
`Console`/`ILogger` calls, `SecretRedactor` runs before every sink, and retention is enforced rather
than left to grow forever. **What P1-2 still owes:** the PostgreSQL credential itself (SSPI vs. a
DPAPI-protected password file, Q1) was not touched by this phase and stays open, still parked for
whichever phase finally does the credential-storage rework.

**The Phase 1B closing review's open question — "does any dependency throw an
`OperationCanceledException` that is not our stopping token?" (`docs/BACKLOG.md`, "Two things the
closing review flagged and could not settle") — is *not* settled by Phase 5.** Every hosted loop now
catches non-cancellation exceptions per tick (`CLAUDE.md` §4, Architecture), but the filter is still
`ex is not OperationCanceledException` — the same shape as before, just applied to more workers. A
dependency that raises `OperationCanceledException` for a reason other than the loop's own
`stoppingToken` still passes straight through uncaught, and `HostOptions.BackgroundServiceExceptionBehavior`
is still the unoverridden .NET default `StopHost` (Phase 5's C-1 finding confirmed this directly), so
that specific failure mode would still take the whole host down. The original suggestion — drive
`IChatNotifier.EditAsync` into a real timeout and see what type comes out — is still the way to settle
it, and is still not done.

### P5-2 — Phase 5 follow-ups (2026-09-26)

| # | Decision | Reasoning |
|---|---|---|
| O-8 | Health checks are our own `ISystemHealthCheck`, not `Microsoft.Extensions.Diagnostics.HealthChecks`. The assembly that owns what is checked implements and registers its checks, scoped; AI keys now lives in `Noof.Ledger.Ai`. One DI scope per run, so the checks run one at a time — today's `HealthCheckService` used a scope per check and ran them in parallel; this is a choice, reversible inside `SystemHealth`. A 5 s cooperative per-check timeout; Disk reads free space off-thread so it can be abandoned. Summaries show the exception type, never its message. A composition test on the real host replaces the central name and category lists | Operator's PR #1 decision: health checks are our own seam. Sharing one scope keeps the checks as plain constructor-injected classes at the cost of running them sequentially instead of in parallel; a repo that is public should never echo a raw exception message to `/diagnostics`, the dashboard tile or Telegram `/health` |
