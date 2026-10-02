---
title: A transfer or exchange read from a bank app screenshot (Wise, Revolut)
status: deferred
area: ai
since: 2026-10-02 (Phase 7 close)
related: [reconciling-with-bank-statements]
---
**Named out of scope by the transfers spec** (`docs/specs/2026-10-01-transfers-and-exchange-design.md`,
"Out of scope"). A screenshot of a completed transfer or conversion in a bank's app states both amounts,
the rate and the fee exactly — better evidence than a spoken message, and the kind of evidence Phase 7
built for menjačnica slips. Today there is no intake for it: a photo goes to `ExtractReceipt`, and
`read_receipt` knows only a fiscal receipt (`sale`, `refund`) or an exchange slip (`exchange`). The
operator records such a transfer by telling it to the bot.

**What building it would take.** A third document kind for vision, read into the same evidence-and-truth
split slips use (evidence kept as read, the `transfers` row as the truth, mapped by C# without the
model), with each bank's screenshot layout described to the prompt. Every strict schema of a request
counts toward the union-typed-parameter limit `read_receipt` is already shaped around (spec A-14). Worth
doing alongside, or after, reconciliation with bank statements, which needs the same per-bank knowledge.
