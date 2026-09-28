---
id: P1-4
title: How much parsing grammar Phase 1 owns
status: decided
date: 2026-09-21
phase: Phase 1
related: [p2-1-quote-and-verify-removed]
---

**Decision:** None. Extraction is the LLM's job.

## Extraction is the model's job, and what that costs

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
2. *(Superseded 2026-09-22 by P2-1.)* CLAUDE.md is absolute that no user-facing number originates from a model. **Resolution:
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
