# Phase 7 — Transfers, currency exchange and foreign-currency spending (design)

**Status:** approved by the operator 2026-10-01 ("spec approved"), then amended the same day after the
planning reviews (Fable 5.1 and a Codex adversarial review, run in parallel) with the operator's
answers to the two decisions they raised (T-12, T-13).

This is Phase 7 ("Currency exchange") in the phase table of `2026-09-22-natural-language-capture.md`.
It amends `2026-09-24-money-model.md` (M5's reserved `Transfer`/`Fee`, M10's unconverted foreign
spending) and `2026-09-25-receipts-design.md` (R-1's deferred exchange-office slips, R-3's payment
defaults), and departs from the original design's §7 where the decisions below say so.

## Goal

Money that moves between the operator's own wallets — a cash withdrawal, a top-up, a transfer between
banks, a currency exchange at a menjačnica or inside a bank — is recorded as what it is: one transfer,
both wallets' balances right, and **no trace of it in spending or income**. A spending in a currency
other than its wallet's is charged to the wallet in the wallet's own currency, at a rate and fee the
operator set on that wallet. An exchange-office slip photographed and sent to the bot becomes an
exchange the same way a fiscal receipt becomes a purchase.

## Decisions (operator's, 2026-10-01)

| # | Decision |
|---|---|
| T-1 | **Scope:** transfers between wallets, currency exchange with a fee, exchange-office slips, spending in a foreign currency. Not in scope: a mid-market rate archive and the spread insight (Q4 stays deferred — there is no external rate source in this phase), loans as liabilities, transfers in transit, reconciliation with bank statements, correcting vision-read fiscal receipts, foreign-currency spending on a fiscal receipt ("нет такого сценария"). |
| T-2 | **One wallet per currency** — cash too ("Cash RSD", "Cash EUR"). A transfer is always between two different wallets, each leg in its wallet's own currency. |
| T-3 | **Approach A:** a transfer's legs live in their own table (`transfers`), a fee is a line item; entries stay derived, never the source of truth. |
| T-4 | **An exchange with neither the received amount nor a rate is not recorded** — the bot asks, and the answer arrives as an ordinary reply correction. |
| T-5 | **A fee is spending**, category *Fees & Charges* (`fees-charges`, already seeded). |
| T-6 | **Foreign-currency spending is converted at the wallet's own terms**, set on `/wallets` per foreign currency: a rate and a fee that may be a percentage, a fixed amount, a minimum, or any combination. *"нужен дефолтный курс, который можно настраивать. пусть будут неточности, это не критично"*; *"все варианты должны быть"*. |
| T-7 | **A stated charge overrides the terms:** *"списали 15400, комиссия 150"* replaces the computed charge and fee. |
| T-8 | **A slip's vision reading is a starting point the operator corrects, like spoken amounts** — *"без вижена это сложно. часто это плохо работает. так что «никогда» не подходит"*. The receipts rule that locks amounts is about **fiscal** receipts; a slip is not one, so its `receipts` row is evidence only and never the source of the transaction's money. Fiscal receipts keep that rule unchanged in this phase (`docs/backlog/correcting-vision-read-receipts.md`). |
| T-9 | **Transfers never appear in spending or income statistics**, and are seen on their own: *"переводы руинят визуальную статистику, кажется что расходы больше чем есть"*. |
| T-10 | **A transfer has one date.** Money in transit for days is backlog (`docs/backlog/transfers-in-transit.md`). |
| T-11 | **Branching:** an aggregate branch `phase-7` — now the general rule for large phases and features (`CLAUDE.md` §5). |
| T-12 | **Stored amounts are what each wallet actually moved**, fees included — what the bank or the purse shows, and what a statement will later be reconciled against. A fee is attributed to one leg; C#, not the model, adds or subtracts it. |
| T-13 | **The Card and Cash payment defaults are per currency**: "Cash RSD" and "Cash EUR" can both be the cash default, each for its own currency. |

### Departures from the original design, and why

- **§7's deterministic grammar is not built.** It predates P2-1 (settled 2026-09-22): the model
  interprets amounts and dates from speech, and the echo plus a correction is the safety. A transfer is
  captured through `record_transaction` like everything else.
- **§7's `FxConversion` with a stored rate and a mid-rate snapshot is not built.** With no mid-market
  source (T-1) there is nothing to snapshot; a transfer's realised rate is derived from its principals
  whenever it is shown.
- **§6's spending predicate** stays as the original design wrote it — expense lines plus fees — but is
  read off line items: the line items of `Expense` transactions, plus the `Fee`-role line items of
  `Transfer` transactions. A fee keeps a category that way.

## 1. Data

**Domain.** `TransactionKind.Transfer = 3` and `EntryRole.Fee = 1` are declared (both were reserved);
`MoneyModelEnumTests` pins the new member counts. `ReceiptKind` gains `Exchange`. `JobKind` gains
`RecordExchange` (§3). `MerchantKind.ExchangeVenue` already exists and is what a slip's office is.

**`transfers`** — one row per `Transfer` transaction, the record's own facts (the `balance_checks`
pattern):
`transaction_id uuid PK FK`, `from_wallet_id`, `from_amount numeric(19,4)`, `from_currency`,
`to_wallet_id`, `to_amount numeric(19,4)`, `to_currency`, `fee_leg smallint null` (`From = 0`,
`To = 1`; null when there is no fee), `stated_rate numeric(24,12) null` with `stated_rate_base`
(the currency the rate is per one unit of; null with it), `venue_merchant_id uuid null`.

- `from_amount` is **everything that left the source wallet**, `to_amount` **everything that reached
  the destination** (T-12). The fee, when there is one, is inside the amount of its leg.
- Checks: both amounts positive; `from_wallet_id <> to_wallet_id`; `fee_leg` null exactly when the
  transfer has no fee line.
- `transactions.wallet_id` holds the source wallet, so code that reads one wallet per transaction
  keeps working.
- The stated rate is kept only so the echo shows the rate the operator said, not one re-derived from
  rounded amounts (`117.0000`, never `116.9999`).

**`wallet_fx_terms`** — a wallet's terms for spending in one foreign currency, PK
`(wallet_id, currency)`: `rate numeric(24,12)` (units of the wallet's currency per one unit of the
foreign currency, positive), `fee_percent numeric(9,4) null` (1.5 means 1.5 %), `fee_fixed
numeric(19,4) null`, `fee_minimum numeric(19,4) null` (both in the wallet's currency). `currency`
differs from the wallet's own (check). Edited on `/wallets`.

**`charges`** — what a spending in a foreign currency cost its wallet, PK
`(transaction_id, currency)`: `charged_amount numeric(19,4)` (the purchase's debit in the wallet's
currency, positive, **without** the commission — the operator's banks post the commission as its own
transaction), `rate_used numeric(24,12)`, the terms snapshot `fee_percent`, `fee_fixed`,
`fee_minimum` (as they were when the charge was computed), and `source smallint` (`WalletTerms = 0`,
`Stated = 1`). For a stated charge `rate_used = charged_amount ÷ the foreign sum`, to twelve
decimals.

- A row freezes the record. Changing a wallet's terms never recomputes past records, and a correction
  of the record recomputes a `WalletTerms` charge from the record's own snapshot — a category-only
  correction never reprices history at today's terms. Only moving the record to another wallet takes
  that wallet's current terms.
- A `Stated` charge survives a correction that does not mention it; the correction's input carries it
  (§2).

**Fees are line items** in `fees-charges`, in the currency they were charged in. `line_items` gains
`role smallint` (`Principal = 0`, `Fee = 1`, the same values as `EntryRole`), because a category alone
cannot tell them apart: a bank fee told as its own message (*"комиссия райф 0.43 евро"*) is an
ordinary `Principal` line in the same category.

- A fee line is written by C#, never by the model, and is replaced on every apply by a delete on
  `role = Fee` — a second delete next to the existing `categorized_by <= Model` one
  (`EfCategorizationStore.ApplyAsync`), so no precedence rule can keep a stale fee alive.
- At most one fee line per transfer and one per foreign currency of a spending.
- A fee line's currency is its wallet's currency; a fee in any other currency is a mapping failure
  (§2).

**Failure reasons.** `transactions` gains `failure_reason smallint null`, written with `Failed` and
cleared when the record completes: `MissingReceivedAmount`, `SameWallet`, `LegCurrencyMismatch`,
`InvalidRate`, `InvalidFee`, `SlipIncomplete`. The echo reads it, so a Cancel/Restore or a replayed
update renders the same specific text rather than the generic failure (§4).

**Payment defaults per currency (T-13).** `wallets`' unique partial index on `default_for_payment`
becomes unique on `(default_for_payment, currency)`. `IWalletDirectory.DefaultForPaymentAsync` takes
the currency. A fiscal receipt is RSD, so R-3 behaves as before.

**Posting** (`LedgerPostings`, in the same database transaction that writes the lines, as today). Each
kind gets an explicit branch, so a new kind can never fall into the expense branch by default:

- **Transfer:** the source gets `Principal = −(from_amount − fee)` when the fee is on the `From` leg,
  else `−from_amount`; the destination gets `Principal = +(to_amount + fee)` when the fee is on the
  `To` leg, else `+to_amount`; the fee's leg wallet gets `Fee = −fee`. Each wallet's entries therefore
  sum to exactly its stored amount.
- **Expense:** `Principal` lines in the wallet's currency → `−sum`, as today; `Principal` lines in a
  foreign currency that has a `charges` row → `−charged_amount` in the wallet's currency; a foreign
  currency with no `charges` row → `−sum` in that currency, as M10 does today; fee lines → `Fee`
  entries on the wallet.
- **Income** and **BalanceCheck:** unchanged.
- **Leaving a kind deletes its facts:** a record that stops being a `Transfer` loses its `transfers`
  row, one that stops being an `Expense` loses its `charges` rows — as `balance_checks` is deleted
  today when a record stops being a `BalanceCheck`.

The `wallet_balances` view and `BalanceSql.AsOfAsync` need no change: both sum entries per wallet and
currency regardless of kind, and a checkpoint on either wallet absorbs only that wallet's leg — the
intended behaviour, covered by tests (backdated transfers, Cancel/Restore, a kind change, a checkpoint
on each side).

The integrity test (`Entries_always_agree_with_the_lines_they_are_summed_from`) is extended: expected
entries are derived from lines, `charges` and `transfers`, and a transfer's second wallet is allowed.

**Arithmetic lives in C#, in `Domain`:**

- converting an amount by a rate, in whichever direction the rate is written;
- the fee — `fee = max(charged × fee_percent / 100 + fee_fixed, fee_minimum)`, each missing term read
  as zero, rounded after the whole formula;
- adding a fee to, or taking it out of, a leg (§2).

Every computed amount is rounded to two decimals, midpoint away from zero. A computed charge that is
not positive (a foreign sum ≤ 0, or one that rounds to zero) is not a charge: that currency stays an
M10 line.

**Revision snapshots** gain `transfer` (both legs, the fee leg, the stated rate, the venue), `charges`
(every column) and each item's `role`. Readers — the trace page's history — accept snapshots written
before this phase, which have none of these keys.

**Migration:** one, additive — the three tables, `line_items.role` (default `Principal`),
`transactions.failure_reason`, the receipts columns of §3, and the per-currency payment-default index.
No backfill: foreign-currency records written before this phase keep their M10 lines until corrected.

## 2. Capture — `record_transaction`

**Schema** (strict: every property required, absent values as `null`; amounts JSON numbers read
straight into `decimal`):

- `kind` gains `transfer`.
- `transfer`: `null`, or `{ from_wallet_id, from_amount, from_currency, to_wallet_id, to_amount,
  to_currency, rate, fee }` — wallet ids from the offered list or `null`; `to_amount` may be `null`;
  `rate` is `null` or `{ base_currency, quote_amount, quote_currency }`, read as "1 base = quote_amount
  quote"; `fee` is `null` or `{ amount, currency, leg, included }` — `leg` is `from` or `to`,
  `included` says whether the amount the operator gave for that leg already accounts for the fee. The
  amounts are the ones the operator said; C# does every addition. A transfer's `items` is empty.
- `charged`: `null`, or `{ amount, currency, fee_amount, fee_included }` (`fee_amount` may be `null`)
  — only when the operator says what was actually charged in the wallet's currency.

**Prompt additions:**

- a withdrawal, a top-up, a transfer between the operator's wallets and a currency exchange are
  `transfer`, never an expense plus an income;
- amounts are copied as said — a rate goes into `rate`, a fee into `fee`, and the model never
  multiplies, adds or subtracts;
- `leg` is `from` unless the operator says the receiving side kept the fee; `included` only when the
  operator says the amount includes it;
- `charged` only when the charged amount is said.

Tuning waits for Phase 11.

**Mapping a transfer** (`ProposalMapper`; C# decides, the model's choices are mapped, never
validated — P2-1). Each rule that cannot be met fails the record with the reason named (§1):

1. **Wallets:** a named wallet as named; otherwise the default wallet of that leg's currency. A leg
   whose currency differs from its wallet's → `LegCurrencyMismatch` (the echo names the wallet to
   create). Both legs on one wallet → `SameWallet`.
2. **Principals.** The source principal is `from_amount`, less the fee when the fee is on `from` and
   `included`. The destination principal, first that applies:
   - `to_amount` stated → that, plus the fee when the fee is on `to` and `included`;
   - same currency → equal to the source principal;
   - a `rate` → the source principal converted by C#. A rate whose base equals its quote, whose pair is
     not the two legs' currencies, or whose `quote_amount` is not positive → `InvalidRate`. A rate on a
     same-currency transfer is ignored.
   - otherwise → `MissingReceivedAmount`.
3. **Stored amounts (T-12):** `from_amount` = source principal + the fee if the fee is on `from`;
   `to_amount` = destination principal − the fee if the fee is on `to`. A fee whose currency is not its
   leg's, or that leaves a principal or a stored amount not positive → `InvalidFee`.
4. **Fee** → one `Fee` line in `fees-charges`, and `fee_leg`.

**Failing.** On a **first reading** the record is `Failed` with its `failure_reason`, and the echo asks
for what is missing (§4). The reply is an ordinary correction and completes the record. On a
**correction**, the record stays exactly as it was (P2-3 — a failed correction never un-books a
record), and the bot answers with the same reason as a correction-failure notice. No new status.

**Mapping a foreign-currency spending** — after the usual mapping, for each `Principal` line currency
other than the wallet's:

1. `charged` given (honoured only when there is exactly one foreign currency) → a `Stated` charge.
   With `fee_included`, the charge is the stated amount less the fee. The fee is the stated
   `fee_amount`, otherwise the wallet's terms applied to the charge.
2. Otherwise, a `Stated` charge already on the record for that currency, **and the record stays on the
   same wallet** (a correction that mentioned neither) → kept, with its fee. A correction that moves
   the record to another wallet discards the old charges and their fees: a figure stated in one
   wallet's currency means nothing in another's.
3. Otherwise, terms — the record's own snapshot on a correction that keeps the wallet, the wallet's
   current terms on a first reading or after a wallet change → `charged = round(sum × rate)`, the fee
   by the formula, source `WalletTerms`.
4. Otherwise → M10 as today, and the echo says how to set a rate (§4).

The charge is computed from the lines the apply actually leaves in place, not from the proposal alone.

**Corrections** re-run the model over the whole record and `ApplyAsync` deletes and replaces, as
today; `transfers` and `charges` rows are rewritten in the same database transaction. The correction's
input carries the record's `Principal` lines only, plus the current transfer (legs, fee and leg,
stated rate) and the current charges as facts of their own. A fee line is never offered back to the
model as an item, so the model cannot echo it as a purchase.

**Kind changes and wallets.** A kind change works both ways (*"это был не расход, а снятие с
райфа"*). The keep-the-record's-wallet rule (`KeepingTheRecordsWallet`) applies per leg:

| From → to | The record's wallet becomes |
|---|---|
| Expense (or BalanceCheck) → Transfer | the source leg |
| Income → Transfer | the destination leg |
| Transfer → Expense | the source leg's wallet |
| Transfer → Income | the destination leg's wallet |
| Transfer → Transfer | each leg keeps its wallet unless the correction names another |

`WalletsIncludingKept` offers the kept wallet of each leg even when archived — only those, never an
unrelated archived wallet. `CategorizationSubject` carries both legs' wallet ids and currencies.

When a record becomes a `Transfer`, every `Principal` line it had is removed, whoever authored it: no
line above `Model` precedence exists today (nothing edits lines by hand yet), and a transfer has no
principal lines by definition. The spending report still filters a transfer's lines by `role = Fee`
explicitly rather than trusting that invariant.

## 3. Exchange-office slips

**Route.** A photo goes to `ExtractReceipt` as today; a slip has no fiscal QR, so it is read by vision.
There is no separate intake: vision says what the document is.

**`read_receipt`** gains `kind = exchange` and an `exchange` object (`null` otherwise): `given_amount`,
`given_currency`, `received_amount`, `received_currency`, `rate` (dinars per one unit of the foreign
currency, as a Serbian slip prints it), `commission_amount`, `commission_currency`, and
`slip_number`.

- Amounts are from the customer's side — given and received, the figures of money that actually
  changed hands — while the slip speaks from the office's ("kupovina / prodaja"); the prompt explains
  the inversion and lists the fields NBS *Odluka* čl. 23 requires on a slip.
- The office's name, PIB and time use the existing `seller`, tax id and `issued_at`. The slip number
  does not go through the fiscal-number pattern, which a *potvrda* number never matches.
- The settled "do not invent" rule applies unchanged: an unreadable field is `null`, and `readable` /
  `unreadable_reason` work as for receipts.

**Storage** — evidence and truth kept apart:

- **Evidence:** a `receipts` row with `Kind = Exchange` and no lines — the office, its PIB, `issued_at`,
  `total`/`currency` holding the dinar side — plus a `receipt_exchanges` row (PK `receipt_id`) holding
  every `exchange` field exactly as vision read it, each nullable, written once and never updated.
  `receipts` gains `slip_number text null` (trimmed, upper-cased), with a unique partial index on
  `(seller_tax_id, slip_number)` for `Kind = Exchange` rows where both are present — the same
  duplicate guard the fiscal index gives receipts.
- **Truth:** the `transfers` row, written only once the exchange is complete, with the office as
  `venue_merchant_id` (a merchant of `MerchantKind.ExchangeVenue`, keyed by its PIB as shops are).

**Recording** — a job of its own, never `CategorizeReceipt`:

1. `ExtractReceiptWorker` saves the evidence and, unless the slip is held or incomplete, enqueues
   `RecordExchange` in the same commit; a caption is enqueued after it in that commit as a `Correct`
   job, so the queue's per-transaction ordering applies it second. On a held slip the caption waits and
   is enqueued, the same way, when "Record anyway" enqueues `RecordExchange`; on an incomplete slip it
   is enqueued at once, since it may carry the missing figure.
2. `RecordExchange` maps the evidence without the model — fills a missing received amount from the
   rate and the given amount when it can, picks the wallets, attributes a printed commission to the leg
   it was taken on — and applies a transfer outcome through `ApplyAsync`, like any other job.
3. **Held** (saved, not recorded, "Record anyway" offered): the dinar side differs from
   `foreign × rate` by more than one para plus the error a rate printed to four decimals can carry
   (`foreign × 0.00005`), after allowing for a printed commission; the PIB is unread or not exactly
   nine digits; the slip number is unread (a duplicate could not be detected).
4. **Incomplete** (an amount or currency unread and not computable from the rate): `Failed` with
   `SlipIncomplete`, and the bot asks for the missing figure. "Record anyway" is never offered for it —
   confirmation cannot supply an amount; only a reply can.
5. "Record anyway" on a held slip enqueues `RecordExchange`. "Awaiting confirmation" for an `Exchange`
   receipt means a held slip only: its transaction is still `Captured` and has no `RecordExchange`
   job. An incomplete slip is `Failed`, and a slip a reply has already completed is `Completed`, so
   neither is ever offered "Record anyway" — on the echo, after Cancel/Restore, or on the trace page.
   (For a fiscal receipt the predicate stays "no `CategorizeReceipt` job".) `ReceiptCategorizationWorker` never claims an `Exchange` receipt, and
   the non-money kinds (Copy/Training/Proforma/Advance) are unchanged.

**Wallets on a first read**, without the model: for each leg, the cash default of that leg's currency
(T-13), else the default wallet of that currency. A slip is always cash.

**Corrections of a slip exchange** run `record_transaction` like any transfer's, with the evidence and
the current transfer as the record's current state; they may change amounts, currencies, the date and
the wallets, and amounts in a caption win over vision's. They are not routed to the receipt path:
`TryRouteToReceiptAsync` routes by one predicate — a fiscal money receipt — which an `Exchange`
receipt does not meet, just as a Copy or Training slip does not. `.claude/rules/receipts.md` is
reworded to say "fiscal receipt" explicitly, rather than gaining an exemption.

## 4. What the operator sees

**Echo** — English (bot-text rule), composed from the database, never from the model's answer (D4).
`CategorizationSubject` gains the second leg and the `transfers`/`charges` facts.

| Record | Echo |
|---|---|
| Transfer | `Transfer — 10 000.00 RSD · Raiffeisen RSD → Cash RSD` and both wallets' balances |
| Transfer with a fee | `Transfer — Raiffeisen RSD −10 150.00 RSD (incl. fee 150.00) → Cash RSD +10 000.00 RSD` |
| Exchange | `Exchange — 100.00 EUR (Cash EUR) → 11 700.00 RSD (Cash RSD) · 1 EUR = 117.0000 RSD`, both balances; a fee shows on its leg as above, and as `Fee 2.00 EUR · Fees & Charges` |
| Slip | as an exchange, plus `Menjačnica <name> · from a slip photo` and the vision warning line receipts carry |
| Foreign spending | as today, plus `30.00 USD → charged 15 600.00 KZT (1 USD = 520.0000 KZT, wallet rate) + fee 156.00 KZT`; `stated` instead of `wallet rate` for a stated charge |
| No terms | `not in the wallet's currency — set a USD rate for Kaspi KZT on /wallets` |
| Failures | one line per reason: `Exchange not recorded: how much RSD did you get? Reply with the amount or the rate.` (`MissingReceivedAmount`); `… both sides are Cash RSD — which wallet did it go to?` (`SameWallet`); `… Cash RSD holds RSD, not EUR — create a EUR wallet or name one` (`LegCurrencyMismatch`); `… couldn't use that rate` (`InvalidRate`); `… couldn't place that fee` (`InvalidFee`); `Slip read, but the received amount is unreadable — reply with it` (`SlipIncomplete`) |
| Source crosses zero | an extra line when this transfer takes the source from ≥ 0 to < 0: `Cash EUR is now −1 000.00 EUR — a missing exchange or income?` — never for a wallet that was already negative, so a credit wallet stays quiet |

A rate is shown as "1 dearer = N cheaper" (N ≥ 1), four decimals; C# picks the direction; a stated
rate is shown as stated, otherwise the rate is derived from the two principals. Cancel and Restore
re-render both balances and the reason text.

**`/wallets`** — each wallet gets *Foreign-currency terms*: a table of currency → rate, fee %, fixed,
minimum, with add, edit and remove; any known currency other than the wallet's own. The Card/Cash
default becomes per currency (T-13). Inline `MudAlert` feedback only (render-mode rule).

**Home — two separate lenses (T-9):**
- **This month — spending and income, never transfer principals.** Each currency's card shows *Spent*
  (the category donut and table, as today) and a new *Received* line (income by category). A foreign
  spending counts in the currency it was bought in; its charge moves the wallet's balance, not the
  statistics. Fees — a transfer's included — appear as their own category.
- **Transfers this month** — a new section: the month's transfers and exchanges
  (`Cash EUR → Cash RSD · 100.00 EUR → 11 700.00 RSD · 1 EUR = 117.0000`) and the fees they cost. No
  donut: an amount moved says nothing about spending.
- **Recent transactions** — *Spending & income* (default) / *Transfers* / *All* as links carrying
  `?view=` (the page renders statically; menus are not allowed), and each row marked by kind — which
  also settles M-6 (income and balance statements looking like spending).
- **Balances** — unchanged; the view already counts transfers.

**`/transactions`** — a URL preset without transfers; the wallet filter matches either leg (through
entries, not `transactions.wallet_id`); the amounts column shows `−100.00 EUR → +11 700.00 RSD`.

**Trace page** — the summary shows both legs, the fee and its leg, the rate, the venue and any charge
with its source and snapshot; the revision history shows the transfer block and still reads snapshots
from before this phase.

**Demo and screenshots** — mock data for a transfer, a transfer with a fee, an exchange, a slip, a
foreign spending and wallet terms; bot scenes for each echo above, a failure included; `/wallets` with
terms and per-currency defaults.

## 5. Delivery

**Branches (T-11, `CLAUDE.md` §5).** `phase-7` is cut from `master`, with the draft PR
`phase-7 → master` (#26). Every PR below targets `phase-7`. A PR that depends on another branches from
that PR's branch until the dependency is merged into `phase-7`, then is retargeted; independent PRs
branch from `phase-7`. Fixes, and whatever the large-model reviews of `phase-7` turn up, land in
`phase-7` as PRs too. When the phase is finalised, `phase-7` merges into `master` — so nothing in
`phase-7` reaches the operator's running app half-built, and a capability may land before its echo.

| # | PR | Assemblies | Depends on |
|---|---|---|---|
| 1a | Model: `Transfer`/`Fee`/`Exchange`/`RecordExchange` enum members, the Domain arithmetic, all tables and columns in one migration | Domain · Persistence | — |
| 1b | Posting branches per kind (leaving a kind deletes its facts), the extended integrity test, revision snapshots, checkpoint tests on two legs | Persistence | 1a |
| 2 | Capture: the `record_transaction` schema, DTO, prompt, `ProposalMapper` (legs, principals, rate, fee leg, failure reasons, charges with snapshot and stated-charge retention), correction input without fee lines, kind-transition wallets, `failure_reason` written | Application · Ai · Host | 1b |
| 3 | Echo: every row of §4's table, Cancel/Restore, `GetSubjectAsync` reading both legs, charges and the failure reason; bot scenes | Application · Persistence | 2 |
| 4a | Wallet terms and per-currency payment defaults: `IWalletFxTerms` store, `DefaultForPaymentAsync(currency)` | Application · Persistence | 1a |
| 4b | `/wallets` terms and per-currency defaults UI; demo data; screenshot | Web | 4a |
| 5 | Read models: income this month, transfers this month, `Kind` in Recent with `view`, the either-leg wallet filter, the trace summary and history | Application · Persistence | 1b |
| 6 | Home, `/transactions` and trace page UI over PR 5; demo data; screenshots | Web | 5 |
| 7a | Slip reading: `read_receipt`'s `exchange` and `slip_number`, the prompt, the DTO | Ai | 1a |
| 7b | Slip recording: evidence storage, `RecordExchange`, held/incomplete, awaiting-confirmation and "Record anyway" for exchanges, the one routing predicate, the receipts rule reworded; the slip scene | Persistence · Host | 2, 3, 7a |
| 8 | Closing the phase per `docs/CLOSING-A-PHASE.md`: status, `CLAUDE.md`, `docs/decisions/p7-1-transfers-and-exchange-decisions.md`, backlog (M10 and M-6 settled) | docs | all |

PR 3 and PR 5 both touch Persistence reads: PR 3 owns `EfCategorizationStore.GetSubjectAsync` (the
echo's subject), PR 5 owns `EfSpendingReadModel`, `EfTransactionList` and `EfTransactionTrace`. A PR
that outgrows the size rule stops and proposes a split, as `CLAUDE.md` §5 says.

**Reviews** (`CLAUDE.md` §1): Fable 5.1 and a Codex adversarial review ran on this spec at planning
(their findings are folded in above); both run again on `phase-7` at the close. One Codex review per
PR — adversarial for 1a, 1b, 2 and 7b, which make design choices; opus at medium effort per task.

## Testing

TDD, a failing test first. No test touches `noof_ledger`, the network or a live model.

- **Domain:** rate direction and conversion, rounding, the fee formula over every present/absent
  combination of its three terms, adding and removing a fee on either leg, a non-positive charge; the
  enum member counts.
- **Ai:** the schema and `tool_choice` asserted on the captured HTTP body; the DTO reads amounts into
  `decimal`; `read_receipt`'s `exchange` object likewise.
- **Mapper and workers** with fakes: every principal rule, every failure reason on a first reading and
  on a correction (the record unchanged), fee on either leg included or not, stated vs snapshot vs
  current terms vs no terms, a stated charge surviving a category-only correction and discarded by a wallet change, every row of the
  kind-transition table, an archived kept leg, the slip route: held, incomplete, "Record anyway", a
  duplicate slip, a caption on a clean, held and incomplete slip, a correction changing an amount, and
  "awaiting confirmation" false for an incomplete and for a reply-completed slip.
- **Persistence** (filtered, template clones): postings per kind with each wallet's entries summing to
  its stored amount, the extended integrity test, facts deleted on leaving a kind, `charges` frozen
  when terms change, the check constraints and the per-currency default index, checkpoints on either
  leg with a backdated transfer, Cancel/Restore and a kind change.
- **M12 extended:** a transfer with a fee and a foreign spending join the end-to-end exactness test
  under `ru-RU` and `sr-Latn-RS`, against literal expected values.
- **E2E (Playwright):** terms and per-currency defaults on `/wallets`; the two lenses on Home and
  `?view=`; `/transactions`'s preset and either-leg filter.
- **Live suite** (opt-in, skipped by default): phrasings of withdrawals, top-ups, exchanges with a rate
  and with a fee on either side, and a stated charge.
- The full suite once, at the end of the phase.

## Acceptance

1. *"поменял 100 евро на 11700 динар"* → a transfer; Cash EUR −100.00, Cash RSD +11 700.00; this
   month's spending and income do not move; the echo shows `1 EUR = 117.0000 RSD`.
2. *"снял 10000 с райфа, комиссия 150"* → Raiffeisen RSD −10 150.00, Cash RSD +10 000.00, a 150.00
   RSD fee in *Fees & Charges*; *"списали 10150 включая комиссию 150"* gives the same figures.
3. *"поменял 100 евро на динары"* → not recorded; the bot asks; the reply *"11700"* records it. The
   same question as a correction of an existing record leaves that record untouched.
4. *"30 долларов с каспи"* with terms 520 and 1 % → Kaspi KZT −15 756.00 (charge 15 600.00, fee
   156.00); *"списали 15400"* in reply → a `Stated` charge of 15 400.00 and a fee of 154.00 by the
   terms; a later category-only correction leaves both unchanged.
5. A synthetic slip photo → an exchange between the per-currency cash wallets with the office as
   venue; the same photo again is caught as a duplicate; a slip whose figures disagree is held for
   "Record anyway"; a slip with an unreadable amount asks for it; a reply correcting an amount is
   applied.
6. Home's *This month* is identical with and without the month's transfer principals (their fees show
   in *Fees & Charges*); *Transfers this month* lists them.

## Out of scope

A mid-market rate archive and the spread (Q4), loans as liabilities (still recorded as
`other-income` — `docs/backlog/deferred-from-phase-4-money-model-and-backup.md`), transfers in transit
(`docs/backlog/transfers-in-transit.md`), reconciliation with bank statements
(`docs/backlog/reconciling-with-bank-statements.md`), correcting vision-read fiscal receipts and editing
receipt lines (`docs/backlog/correcting-vision-read-receipts.md`), foreign-currency spending on a
fiscal receipt (T-1; the charge arithmetic is a shared `Domain` operation, so the receipt path can use
it later without redesign), several fees on one transfer, a transfer from a bank screenshot
(Wise/Revolut), and bulk tools for turning earlier expense + income pairs into transfers (a reply
correction does it one record at a time).
