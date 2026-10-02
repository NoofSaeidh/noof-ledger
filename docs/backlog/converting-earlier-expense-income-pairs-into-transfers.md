---
title: Turning earlier expense + income pairs into transfers, in bulk
status: deferred
area: persistence
since: 2026-10-02 (Phase 7 close)
---
**Named out of scope by the transfers spec** (`docs/specs/2026-10-01-transfers-and-exchange-design.md`,
"Out of scope"). Before Phase 7, money moved between wallets could only be recorded as an expense from
one and an income to the other, and both then count in the spending and income statistics (T-9). A reply
correction turns one such record into a transfer (*"это был не расход, а снятие с райфа"* — the
kind-transition table in spec §2), but its other half stays and has to be cancelled on its own, and
nothing finds the pairs.

**Why not built.** The operator is not recording real spending in the ledger yet, so there is no history
of pairs to convert. A bulk tool would have to match pairs (same day, amounts that agree, two different
wallets), show them for confirmation, and turn each pair into one transfer while cancelling the other
half — a dashboard feature with its own revisions, worth building only once real history shows the pairs
exist.
