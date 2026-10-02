---
title: Reconciling what was captured with the bank's statement
status: deferred
area: other
since: 2026-10-01 (Phase 7 brainstorming)
related: [deferred-from-phase-4-money-model-and-backup, deferred-from-phase-6-receipts]
---
**What the operator asked for (2026-10-01):** check the records told to the bot in the moment against
the bank's own statement, and bring them into agreement. **Scope is every record, not only Phase 7's**
(operator: "сверка не только этой фазы но любых данных") — expenses, income, receipts, transfers,
exchanges and foreign-currency charges alike: anything that moved money in a wallet the statement
covers.

**Why Phase 7 makes it more pressing.** Phase 7 posts a spending in a foreign currency at the wallet's
own configured rate and fee (`wallet_fx_terms`), and the operator accepted that this is approximate
("пусть будут неточности, это не критично"). Balance statements already correct the balance, but not
the individual records: a record keeps its estimated charge and fee forever unless corrected by hand.
A statement carries the real figures — the amount actually charged in the wallet's currency, the
bank's separate commission line, the posting date — so it is the natural source for replacing
estimates with facts.

**Shape, as far as it is known now:**
- Import a statement export per bank (Raiffeisen, Kaspi, Wise, …) into one wallet — the format and
  the intake surface (upload on the dashboard, a file sent to the bot) are undecided.
- Match each statement row to a recorded transaction: same wallet, amount in the wallet's currency
  within a tolerance, date within a window (a card payment often posts a day or two later). A bank's
  separate commission row matches the record's fee line, not a transaction of its own.
- Show three lists: matched, in the statement but never captured, captured but absent from the
  statement (duplicates, cancelled-but-not-cancelled, wrong wallet).
- Applying a match replaces an estimate with the statement's figure — for a foreign-currency charge,
  a new `charges.source` value (`Statement`) next to `WalletTerms` and `Stated`; for a fee line, the
  real commission. Every change goes through the revision history like any other correction, and
  nothing is applied without the operator confirming it.
- A statement's closing balance can become a balance checkpoint (M6) for that wallet.

**Constraints already settled that this must respect:** real statements never enter the repo (test
fixtures stay synthetic, `.gitignore` covers statement files); a record's amounts are not silently
overwritten — the operator confirms; a receipt's amounts still come only from the fiscal record or
the vision read (`.claude/rules/receipts.md`), so a statement row may correct the *charge* on the
wallet, never a receipt line.
