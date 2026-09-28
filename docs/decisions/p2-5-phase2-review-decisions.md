---
id: P2-5
title: "Operator review of Phase 2 (2026-09-23)"
status: decided
date: 2026-09-23
phase: Phase 2
---

The operator reviewed the finished phase and made six decisions, since implemented (see
`CLAUDE.md` §3/§4). Recorded here for the reasoning; the rules themselves live in `CLAUDE.md` and
`.claude/rules/`.

| # | Decision | Reasoning |
|---|---|---|
| D-A | Nothing depends on the LLM provider except its `IChatClientFactory` implementation, confined to `src/Noof.Ledger.Ai/Anthropic/` (not a separate project) | A provider swap should touch one folder, not a project boundary that would force every provider-neutral type to move with it; `AiBoundaryTests` enforces it by scanning source text, not by convention |
| D-B | The tool loop runs through `Microsoft.Extensions.AI`'s `FunctionInvokingChatClient`, not a hand-written loop | The framework already implements the request/response/tool-result cycle correctly; a hand loop duplicated that logic with no behavioural difference to defend |
| D-C | Public services go through an interface, except simple helpers named individually | A substitutable seam needs a name other than its one implementation; `MerchantName.Fold` and `SecretKeys` are stateless helpers with nothing to substitute, so an interface there would be ceremony, not a seam |
| D-D | The bot writes English only for now, including the category name shown in the echo (`NameEn`) | Multi-language is real scope — resource strings, a language setting — that Phase 2 never budgeted; shipping one language now and widening later costs nothing today, and the operator may still write to the bot in any language |
| D-E | Identifiers and comments use English action names (`Cancel`/`Edit`/`Restore`), never the Russian UI labels | A Russian identifier reads as Russian-only code to anyone who does not read Russian; the UI text and the code's own names are different axes, and only one of them needed to change for D-D |
| D-F | Currencies outside the five known codes: deferred, docs only | The strict schema's enum is a compile-time set; widening it safely means splitting `CurrencyCode.Supported` into nameable vs. rateable and reopening Q4 (the rate-source question), which is Phase 4/7 work, not a Phase 2 fix — `docs/backlog/currencies-outside-the-five-known-codes.md` |

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
