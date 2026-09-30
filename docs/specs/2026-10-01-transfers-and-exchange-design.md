# Phase 7 — Transfers, currency exchange and foreign-currency spending (design)

**Status:** approved in conversation 2026-10-01, section by section; this written spec awaits the
operator's review before the implementation plan is written.

This is Phase 7 ("Currency exchange") in the phase table of `2026-09-22-natural-language-capture.md`.
It amends `2026-09-24-money-model.md` (M5's reserved `Transfer`/`Fee`, M10's unconverted foreign
spending) and `2026-09-25-receipts-design.md` (R-1's deferred exchange-office slips), and departs from
the original design's §7 where the decisions below say so.

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
| T-1 | **Scope:** transfers between wallets, currency exchange with a fee, exchange-office slips, spending in a foreign currency. Not in scope: a mid-market rate archive and the spread insight (Q4 stays deferred — there is no external rate source in this phase), loans as liabilities, transfers in transit, reconciliation with bank statements, correcting vision-read fiscal receipts. |
| T-2 | **One wallet per currency** — cash too ("Cash RSD", "Cash EUR"). A transfer is always between two different wallets. |
| T-3 | **Approach A:** a transfer's legs live in their own table (`transfers`), a fee is a line item; entries stay derived, never the source of truth. |
| T-4 | **An exchange with neither the received amount nor a rate is not recorded** — the bot asks, and the answer arrives as an ordinary reply correction. |
| T-5 | **A fee is spending**, category *Fees & Charges* (`fees-charges`, already seeded). |
| T-6 | **Foreign-currency spending is converted at the wallet's own terms**, set on `/wallets` per foreign currency: a rate and a fee that may be a percentage, a fixed amount, a minimum, or any combination. *"нужен дефолтный курс, который можно настраивать. пусть будут неточности, это не критично"*; *"все варианты должны быть"*. |
| T-7 | **A stated charge overrides the terms:** *"списали 15400, комиссия 150"* replaces the computed charge and fee. |
| T-8 | **A slip's vision reading is a starting point the operator corrects, like spoken amounts** — *"без вижена это сложно. часто это плохо работает. так что «никогда» не подходит"*. Slips are exempt from the receipts rule that locks amounts; fiscal receipts keep that rule unchanged in this phase (`docs/backlog/correcting-vision-read-receipts.md`). |
| T-9 | **Transfers never appear in spending or income statistics**, and are seen on their own: *"переводы руинят визуальную статистику, кажется что расходы больше чем есть"*. |
| T-10 | **A transfer has one date.** Money in transit for days is backlog (`docs/backlog/transfers-in-transit.md`). |
| T-11 | **Branching:** an aggregate branch `phase-7`; every PR of the phase targets it and is reviewed on its own; fixes and the large-model reviews' follow-ups land there too; `phase-7` merges into `master` once finalised (see *Delivery*). |

### Departures from the original design, and why

- **§7's deterministic grammar is not built.** It predates P2-1 (settled 2026-09-22): the model
  interprets amounts and dates from speech, and the echo plus a correction is the safety. A transfer is
  captured through `record_transaction` like everything else.
- **§7's `FxConversion` with a stored rate and a mid-rate snapshot is not built.** With no mid-market
  source (T-1) there is nothing to snapshot; the realised rate is derived from the two amounts whenever
  it is shown, never stored.
- **§6's "Spending is `Kind == Expense || Role == Fee`" becomes "the line items of `Expense` and
  `Transfer` transactions".** A transfer's only line items are its fees, so the existing line-item
  report covers fees without a second predicate, and a fee keeps a category.

## 1. Data

**Domain.** `TransactionKind.Transfer = 3` and `EntryRole.Fee = 1` are declared (both were reserved);
`MoneyModelEnumTests` pins the new member counts. `ReceiptKind` gains `Exchange`.

**`transfers`** — one row per `Transfer` transaction, the record's own facts (the `balance_checks`
pattern):
`transaction_id uuid PK FK`, `from_wallet_id`, `from_amount numeric(19,4)`, `from_currency`,
`to_wallet_id`, `to_amount numeric(19,4)`, `to_currency`, `venue_merchant_id uuid null` (the
exchange office from a slip, a merchant keyed by its PIB as shops are). Checks: both amounts positive,
`from_wallet_id <> to_wallet_id`. `transactions.wallet_id` holds the source wallet, so code that reads
one wallet per transaction keeps working. The rate is not stored.

**`wallet_fx_terms`** — a wallet's terms for spending in one foreign currency, PK
`(wallet_id, currency)`: `rate numeric(24,12)` (units of the wallet's currency per one unit of the
foreign currency), `fee_percent numeric(9,4) null` (1.5 means 1.5 %), `fee_fixed numeric(19,4) null`,
`fee_minimum numeric(19,4) null` (both in the wallet's currency). `currency` differs from the wallet's
own (check). Edited on `/wallets`.

**`charges`** — what a spending in a foreign currency cost its wallet, PK
`(transaction_id, currency)`: `charged_amount numeric(19,4)` (in the wallet's currency, positive),
`rate_used numeric(24,12)`, `source smallint` (`WalletTerms = 0`, `Stated = 1`). A row freezes the
record: changing a wallet's terms never recomputes past records; a correction of that record does.

**Fees are line items** in `fees-charges`, in the currency they were charged in — a transfer's stated
fee, a foreign spending's computed or stated fee. `line_items` gains `role smallint` (`Principal = 0`,
`Fee = 1`, the same values as `EntryRole`), because a category alone cannot tell them apart: a bank fee
told as its own message (*"комиссия райф 0.43 евро"*) is an ordinary `Principal` line in the same
category. A fee line is written by C#, never by the model, and every apply replaces it along with the
model's lines, whatever its `categorized_by`.

**Posting** (`LedgerPostings`, in the same database transaction that writes the lines, as today). Each
kind gets an explicit branch, so a new kind can never fall into the expense branch by default:

- **Transfer:** `−from_amount` on the source wallet and `+to_amount` on the destination wallet (role
  `Principal`); each fee line → a `Fee` entry of minus its amount, on the leg whose currency matches the
  fee's (the source first), else on the source wallet.
- **Expense:** `Principal` lines in the wallet's currency → `−sum`, as today; `Principal` lines in a
  foreign currency that has a `charges` row → `−charged_amount` in the wallet's currency; a foreign
  currency with no `charges` row → `−sum` in that currency, as M10 does today; fee lines → `Fee`
  entries on the wallet.
- **Income** and **BalanceCheck:** unchanged.

The `wallet_balances` view needs no change: it sums entries per wallet and currency regardless of kind.
The integrity test (`Entries_always_agree_with_the_lines_they_are_summed_from`) is extended to derive
the expected entries from lines, `charges` and `transfers`, and to allow a transfer's second wallet.

**Arithmetic lives in C#, in `Domain`:** converting an amount by a rate, the direction of a rate, and
the fee — `fee = max(charged × fee_percent / 100 + fee_fixed, fee_minimum)`, each missing term read as
zero, rounded after the whole formula. Every computed amount is rounded to two decimals, midpoint away
from zero.

**Revision snapshots** gain `transfer` (both legs, the venue), `charges` (currency, charged amount,
rate used, source) and each item's `role`.

**Migration:** one, additive — the three tables and `line_items.role` (default `Principal`). No
backfill: foreign-currency records written before this phase keep their M10 lines until corrected.

## 2. Capture — `record_transaction`

**Schema** (strict: every property required, absent values as `null`; amounts JSON numbers read
straight into `decimal`):

- `kind` gains `transfer`.
- `transfer`: `null`, or `{ from_wallet_id, from_amount, from_currency, to_wallet_id, to_amount,
  to_currency, rate, fee }` — wallet ids from the offered list or `null`; `to_amount` may be `null`;
  `rate` is `null` or `{ base_currency, quote_amount, quote_currency }`, read as "1 base = quote_amount
  quote"; `fee` is `null` or `{ amount, currency }`. A transfer's `items` is empty.
- `charged`: `null`, or `{ amount, currency, fee_amount }` (`fee_amount` may be `null`) — only when the
  operator says what was actually charged in the wallet's currency.

**Prompt additions:** a withdrawal, a top-up, a transfer between the operator's wallets and a currency
exchange are `transfer`, never an expense plus an income; a rate goes into `rate` and the model never
multiplies; `charged` only when the charged amount is said. Tuning waits for Phase 11.

**Mapping a transfer** (`ProposalMapper`; C# decides, the model's choices are mapped, never
validated — P2-1):

1. **Wallets:** a named wallet as named; otherwise the default wallet of that leg's currency. Both legs
   on one wallet → the record fails with a message saying so.
2. **Received amount**, first that applies: stated → as stated; same currency → equal to the amount
   given; a `rate` → C# converts, in whichever direction the rate is written; otherwise the new failure
   reason `MissingReceivedAmount`.
3. **Failure** leaves the record `Failed`, as any unmappable reading does today, with its own echo
   (§4); the operator's reply is an ordinary correction and completes the exchange. No new status.
4. **Fee** → one line item in `fees-charges`, in the fee's currency.

**Mapping a foreign-currency spending** — after the usual mapping, for each line currency other than
the wallet's:

1. `charged` given (only honoured when there is exactly one foreign currency) → a `Stated` charge; the
   fee is the stated `fee_amount`, otherwise the wallet's terms applied to the stated charge.
2. Otherwise, the wallet has terms for that currency → `charged = round(sum × rate)`, fee by the
   formula, source `WalletTerms`.
3. Otherwise → M10 as today, and the echo says how to set a rate (§4).

**Corrections** re-run the model over the whole record and `ApplyAsync` deletes and replaces, as
today; `transfers` and `charges` rows are rewritten in the same database transaction. The model sees
the current transfer (both legs) alongside the current lines. A kind change works both ways (*"это был
не расход, а снятие с райфа"*). The keep-the-record's-wallet rule (`KeepingTheRecordsWallet`) applies
to each leg: a leg the correction does not name keeps its wallet, archived or not.

## 3. Exchange-office slips

**Route.** A photo goes to `ExtractReceipt` as today; a slip has no fiscal QR, so it is read by vision.
There is no separate intake: vision says what the document is.

**`read_receipt`** gains `kind = exchange` and an `exchange` object (`null` otherwise): `given_amount`,
`given_currency`, `received_amount`, `received_currency`, `rate` (dinars per one unit of the foreign
currency, as a Serbian slip prints it), `commission_amount`, `commission_currency`. Amounts are from
the customer's side — given and received — while the slip speaks from the office's ("kupovina /
prodaja"); the prompt explains the inversion and lists the fields NBS *Odluka* čl. 23 requires on a
slip. The office's name, PIB, slip number and time use the existing `seller`, tax id, `fiscal_number`
and `issued_at`. The settled "do not invent" rule applies unchanged: an unreadable field is `null`, and
`readable` / `unreadable_reason` work as for receipts.

**Storage.** A `receipts` row with `Kind = Exchange` and no lines — the office, its PIB, the slip
number (so the existing `(seller_tax_id, fiscal_number)` duplicate check stops a slip photographed
twice), and `total`/`currency` holding the dinar side — plus the `transfers` row with
`venue_merchant_id`. The `receipts` row records what vision read; the `transfers` row is the current
truth; the `Initial` revision snapshot keeps vision's full reading.

**Checks — a guard against an obviously bad read, not a guarantee.** The slip is saved but held for
"Record anyway" (the existing flow) when the dinar side differs from `foreign × rate` by more than one
para plus the error a rate printed to four decimals can carry (`foreign × 0.00005`), when any of the
four amounts or currencies is unread, or when the PIB is not exactly nine digits.

**Wallets on a first read**, without the model: for each leg, the wallet marked `Cash` for payment if
it is in that leg's currency, else the default wallet of that currency (a slip is always cash). A
caption, if any, is applied as a correction of that first reading — and amounts it states win over
vision's.

**Corrections of a slip exchange are not routed to the receipt path.** `TryRouteToReceiptAsync` skips
an `Exchange` receipt; the correction runs `record_transaction` like any transfer's, and may change
amounts, currencies, the date and the wallets. `.claude/rules/receipts.md` records the exemption;
fiscal receipts keep today's rule.

**A commission** printed on the slip becomes a fee line in `fees-charges`, as for any transfer.

## 4. What the operator sees

**Echo** — English (bot-text rule), composed from the database, never from the model's answer (D4).
`CategorizationSubject` gains the second leg and the `transfers`/`charges` facts.

| Record | Echo |
|---|---|
| Transfer | `Transfer — 10 000.00 RSD · Raiffeisen RSD → Cash RSD` and both wallets' balances |
| Exchange | `Exchange — 100.00 EUR (Cash EUR) → 11 700.00 RSD (Cash RSD) · 1 EUR = 117.0000 RSD`, both balances; a fee adds `Fee 2.00 EUR · Fees & Charges` |
| Slip | as an exchange, plus `Menjačnica <name> · from a slip photo` and the vision warning line receipts carry |
| Foreign spending | as today, plus `30.00 USD → charged 15 600.00 KZT (1 USD = 520.0000 KZT, wallet rate) + fee 156.00 KZT`; `stated` instead of `wallet rate` for a stated charge |
| No terms | `not in the wallet's currency — set a USD rate for Kaspi KZT on /wallets` |
| No received amount | `Exchange not recorded: how much RSD did you get? Reply with the amount or the rate.` |
| Source goes negative | an extra line: `Cash EUR is now −1 000.00 EUR — a missing exchange or income?` |

A rate is always shown as "1 dearer = N cheaper" (N ≥ 1), four decimals; C# picks the direction.
Cancel and Restore re-render both balances.

**`/wallets`** — each wallet gets *Foreign-currency terms*: a table of currency → rate, fee %, fixed,
minimum, with add, edit and remove; any known currency other than the wallet's own. Inline `MudAlert`
feedback only (render-mode rule).

**Home — two separate lenses (T-9):**
- **This month — spending and income, never transfers.** Each currency's card shows *Spent* (the
  category donut and table, as today) and a new *Received* line (income by category). A foreign
  spending counts in the currency it was bought in; its charge moves the wallet's balance, not the
  statistics. Fees appear as their own category.
- **Transfers this month** — a new section: the month's transfers and exchanges
  (`Cash EUR → Cash RSD · 100.00 EUR → 11 700.00 RSD · 1 EUR = 117.0000`) and the fees they cost. No
  donut: an amount moved says nothing about spending.
- **Recent transactions** — *Spending & income* (default) / *Transfers* / *All* as links carrying
  `?view=` (the page renders statically; menus are not allowed), and each row marked by kind — which
  also settles M-6 (income and balance statements looking like spending).
- **Balances** — unchanged; the view already counts transfers.

**`/transactions`** — a URL preset without transfers; the wallet filter matches either leg (through
entries, not `transactions.wallet_id`); the amounts column shows `−100.00 EUR → +11 700.00 RSD`.

**Trace page** — the summary shows both legs, the rate, the venue and any charge with its source; the
revision history shows the transfer block.

**Demo and screenshots** — mock data for a transfer, an exchange, a slip, a foreign spending and wallet
terms; bot scenes for each echo above, the missing-amount question included; `/wallets` with terms.

## 5. Delivery

**Branches (T-11).** `phase-7` is cut from `master`, with a draft PR `phase-7 → master` opened at once.
Every PR below targets `phase-7`. A PR that depends on another branches from that PR's branch until
the dependency is merged into `phase-7`, then is retargeted; independent PRs branch from `phase-7`.
Fixes, and whatever the large-model reviews of `phase-7` turn up, land in `phase-7` as PRs too. When
the phase is finalised, `phase-7` merges into `master`.

| # | PR | Assemblies | Depends on |
|---|---|---|---|
| 1 | Model: `Transfer`/`Fee`, the three tables and the migration, posting branches, the integrity test, revision snapshots, the conversion and fee arithmetic, the `wallet_fx_terms` store | Domain · Persistence | — |
| 2 | Capture: the `record_transaction` schema, DTO, `ProposalMapper` (legs, rate, `MissingReceivedAmount`, charges), prompt, keep-the-wallet per leg | Application · Ai · Host | 1 |
| 3 | Echo: every row of §4's table, Cancel/Restore, `GetSubjectAsync` reading the second leg; bot scenes | Application · Persistence | 2 |
| 4 | `/wallets` foreign-currency terms; demo data; screenshot | Web | 1 |
| 5 | Read models: income this month, transfers this month, `Kind` in Recent with `view`, the either-leg wallet filter, the trace summary | Application · Persistence | 1 |
| 6 | Home, `/transactions` and trace page UI over PR 5; demo data; screenshots | Web | 5 |
| 7 | Slips: `read_receipt`'s `exchange`, the prompt, `ReceiptKind.Exchange`, the `ExtractReceiptWorker` route, the checks, the correction-routing exemption, the receipts rule; the slip scene | Domain · Ai · Host | 2 |
| 8 | Closing the phase per `docs/CLOSING-A-PHASE.md`: status, `CLAUDE.md`, `docs/decisions/p7-1-transfers-and-exchange-decisions.md`, backlog (M10 and M-6 settled) | docs | all |

A PR that outgrows the size rule stops and proposes a split, as `CLAUDE.md` §5 says.

**Reviews** (`CLAUDE.md` §1): Fable 5.1 on this spec at planning, in parallel with a Codex adversarial
review of the same scope, and again on `phase-7` at the close; one Codex review per PR — adversarial
for PRs 1, 2 and 7, which make design choices; opus at medium effort per task.

## Testing

TDD, a failing test first. No test touches `noof_ledger`, the network or a live model.

- **Domain:** rate direction and conversion, rounding, the fee formula over every present/absent
  combination of its three terms; the enum member counts.
- **Ai:** the schema and `tool_choice` asserted on the captured HTTP body; the DTO reads amounts into
  `decimal`; `read_receipt`'s `exchange` object likewise.
- **Mapper and workers** with fakes: every received-amount rule, `MissingReceivedAmount`, both legs on
  one wallet, stated vs terms vs no terms, a correction that changes kind, keep-the-wallet per leg, the
  slip route and its checks, a slip correction changing an amount.
- **Persistence** (filtered, template clones): postings per kind, the extended integrity test,
  `charges` frozen when terms change, the check constraints, balances through the view with transfers.
- **M12 extended:** a transfer and a foreign spending join the end-to-end exactness test under `ru-RU`
  and `sr-Latn-RS`, against literal expected values.
- **E2E (Playwright):** terms on `/wallets`; the two lenses on Home and `?view=`; `/transactions`'s
  preset and either-leg filter.
- **Live suite** (opt-in, skipped by default): phrasings of withdrawals, top-ups, exchanges with a rate
  and with a fee, and a stated charge.
- The full suite once, at the end of the phase.

## Acceptance

1. *"поменял 100 евро на 11700 динар"* → a transfer; both balances move; this month's spending and
   income do not; the echo shows `1 EUR = 117.0000 RSD`.
2. *"снял 10000 с райфа"* → a same-currency transfer from the Raiffeisen wallet to the cash wallet.
3. *"поменял 100 евро на динары"* → not recorded; the bot asks; the reply *"11700"* records it.
4. *"30 долларов с каспи"* with terms 520 and 1 % → −15 600.00 KZT and a 156.00 KZT fee line;
   *"списали 15400"* in reply → a `Stated` charge.
5. A synthetic slip photo → an exchange from the cash wallets with the office as venue; a slip whose
   figures disagree is held for "Record anyway"; a reply correcting its amount is applied.
6. Home's *This month* is identical with and without the month's transfers; *Transfers this month*
   lists them.

## Out of scope

A mid-market rate archive and the spread (Q4), loans as liabilities (still recorded as
`other-income` — `docs/backlog/deferred-from-phase-4-money-model-and-backup.md`), transfers in transit
(`docs/backlog/transfers-in-transit.md`), reconciliation with bank statements
(`docs/backlog/reconciling-with-bank-statements.md`), correcting vision-read fiscal receipts and editing
receipt lines (`docs/backlog/correcting-vision-read-receipts.md`), a transfer from a bank screenshot
(Wise/Revolut), and bulk tools for turning earlier expense + income pairs into transfers (a reply
correction does it one record at a time).
