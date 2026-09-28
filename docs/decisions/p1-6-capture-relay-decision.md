---
id: P1-6
title: "The capture relay: the cloud receives, the PC drains"
status: superseded
date: 2026-09-21
superseded_by: p1-6-stay-local-only
---

**Decided 2026-09-21**, after two research passes (7 areas then 6, each independently verified; the
second flagged **28 quoted prices that their own citations did not support**, which is the reason nothing
below is repeated from memory).

## The decision

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

## What this honestly does and does not buy

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

## Shape

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

## The one assumption nobody could verify from documentation

**Whether Telegram accepts the TLS certificate of a `*.lambda-url.<region>.on.aws` hostname.** No primary
AWS or Telegram page states it. It is the single biggest unknown under this plan, it is settled by a
two-hour spike with a throwaway bot, and **if it fails the plan changes shape** (an API Gateway custom
domain, or Azure, would be the fallback). Do that spike before writing anything else.

Second unknown, and it interacts with the first: **cold-start latency** of `dotnet10` for a function
invoked a few dozen times a day. Because the retry budget is undocumented, a slow cold start is a
correctness question rather than a latency curiosity. SnapStart is available on `dotnet10` if needed.

## Addendum — two follow-up decisions, 2026-09-21

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
