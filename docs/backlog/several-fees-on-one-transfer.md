---
title: Several fees on one transfer
status: deferred
area: persistence
since: 2026-10-02 (Phase 7 close)
---
**Named out of scope by the transfers spec** (`docs/specs/2026-10-01-transfers-and-exchange-design.md`,
"Out of scope"). A transfer carries at most one fee: `record_transaction`'s `transfer.fee` is one
`{amount, currency, leg, included}`, `transfers.fee_leg` names one leg, and the write path keeps at most
one `Fee` line per transfer (spec §1). A transfer that cost two fees — the sending bank's and the
receiving bank's, or a menjačnica's commission plus a card fee — is told today as one fee on one leg, or
the second fee as its own message, which records an ordinary *Fees & Charges* spending on its wallet.
The balances come out right either way; only the link between the second fee and its transfer is lost.

**What building it would take.** A fee per leg (or a list) in the tool schema, the leg becoming a
property of each fee line rather than of the transfer, the posting and the echo showing both, and the
correction input carrying each. `record_transaction` already sits near Anthropic's strict-schema limit on
union-typed parameters (A-14), so a second fee object means reshaping the schema, not
adding a field.
