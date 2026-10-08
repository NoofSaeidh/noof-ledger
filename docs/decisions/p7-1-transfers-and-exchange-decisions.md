---
id: P7-1
title: "Phase 7 transfers, exchange and foreign-currency spending decisions (2026-10-01)"
status: decided
date: 2026-10-01
phase: Phase 7
related: [p4-1-money-model-decisions, p6-1-receipts-decisions, q4-fx-rate-source]
---

Full design in `docs/specs/2026-10-01-transfers-and-exchange-design.md`; the rules that bind future
work live in `CLAUDE.md` and `.claude/rules/` (`receipts.md` for the fiscal-receipt-versus-slip
wording, `model.md` for the model never doing arithmetic on money). The operator's thirteen decisions
(T-1..T-13), taken at the design, the last two after the planning reviews:

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

## Departures from the original design

The original design (`docs/specs/2026-09-19-noof-finance-design.md`) described transfers and exchange
in §6–§7. Two of its pieces were deliberately not built, and one was kept but read differently:

- **§7's deterministic grammar is not built.** It predates P2-1 (settled 2026-09-22): the model
  interprets amounts and dates from speech, and the echo plus a correction is the safety. A transfer is
  captured through `record_transaction` like everything else.
- **§7's `FxConversion` with a stored rate and a mid-rate snapshot is not built.** With no mid-market
  source (T-1, Q4) there is nothing to snapshot; a transfer's realised rate is derived from its
  principals whenever it is shown, and only a rate the operator said is stored (`transfers.stated_rate`),
  so the echo shows it as said.
- **§6's spending predicate** stays as the original design wrote it — expense lines plus fees — but is
  read off line items: the line items of `Expense` transactions, plus the `Fee`-role line items of
  `Transfer` transactions. A fee keeps a category that way.

## Planning amendments (operator, 2026-10-01)

Taken while the implementation plan was written against the code, and folded into the spec the same
day; copied here verbatim from the spec's own table.

| # | Amendment | Why |
|---|---|---|
| A-1 | The PR cut is §5's table (17 PRs). A PR may exceed the ~500-line guide — it is a recommendation, not a limit. | The code put charges and the correction input in Persistence, and slips across three assemblies. |
| A-2 | `charges.fee_amount numeric(19,4) NOT NULL` — the fee a charge cost, 0 when none. | A fee line is replaced on every apply, so a stated fee had nowhere to survive a correction. |
| A-3 | A seventh failure reason, `InvalidAmount`: a transfer amount (said, converted or left after the fee) that is not positive. | Otherwise the `transfers` check constraint rejects the write, it is retried as transient and ends as a generic failure. |
| A-4 | Enum columns are `integer`, as every enum column in the schema is. | The `smallint` above was illustrative. |
| A-5 | Echo amounts keep the bot's format: `0.00` invariant, ASCII minus, no grouping (`10000.00`, `-1000.00`). | As Phase 4; the figures in §4 are illustrative. |
| A-6 | A failure-reason echo names only what the database holds — only the reason is stored, not the rejected proposal: `Transfer not recorded: both sides are the same wallet — which wallet did it go to?`, `Transfer not recorded: a wallet holds another currency — create a wallet in that currency or name one.`, `Exchange not recorded: how much did you get? Reply with the amount or the rate.` (the currency is named when the record already holds a transfer), `Slip read, but the amount received is unreadable — reply with it.` (from the slip's evidence). | So Cancel/Restore and a replayed update render exactly the same text. |
| A-7 | "Awaiting confirmation" for an `Exchange` receipt = no `RecordExchange` job, never applied (no `Initial`/`Correction`/`Edit` revision) and `failure_reason` `None`; status-independent like the fiscal predicate. | Cancel → Restore of a held slip returns to its prompt; an incomplete slip and a completed-then-cancelled one never offer "Record anyway". |
| A-8 | A reply to a held slip records it, as for a fiscal receipt. A reply that completes a held or incomplete slip puts each unnamed leg on the cash default of its currency, by the same code `RecordExchange` uses. A slip correction keeps its office. | A reply is the operator's own figures; the slip rule (always cash) must not depend on the model. |
| A-9 | A slip whose PIB already belongs to a shop links to that merchant; an unknown PIB creates an `ExchangeVenue`. `ix_merchants_tax_id` is unchanged. | One PIB is one legal entity. |
| A-10 | A slip's caption job is created 1 µs after its `RecordExchange` job. | The queue orders a transaction's jobs by strict `created_at <`; two jobs of one commit share `now`. |
| A-11 | An `Exchange` receipt with an unread dinar side stores `total = 0`; `receipts.total` stays NOT NULL. Every currency of a slip's reading is a three-letter code or null. | `receipt_exchanges` is the evidence and `transfers` the truth; nothing reads a slip's `receipts.total`. |
| A-12 | A slip's warning line is `⚠️ Read from the slip photo — check the figures.`; a slip correction shows the model a text built from the evidence instead of the caption, so the caption arrives once, as the instruction. | The fiscal QR advice means nothing for a slip. |
| A-13 | A stated charge in a currency other than the wallet's is not honoured; the terms apply and the echo says `wallet rate`. A stated charge that "includes" an unstated fee is solved exactly: fee = max((p·stated + fixed) / (1 + p), minimum), charge = stated − fee. | The fee is then the terms' fee on the charge that is left. |
| A-14 | Strict tool schemas stay within Anthropic's limit of 16 union-typed parameters per request, each pinned by a test: `read_receipt`'s `exchange` is a required object whose fields are null for a non-slip, with the commission as one nullable `{amount, currency}`; `record_transaction` keeps §2's shape at 15. | Past the limit every call fails, fiscal receipts included (https://platform.claude.com/docs/en/build-with-claude/structured-outputs, "Schema complexity limits": also 20 strict tools and 24 optional parameters, all counted across every strict schema of one request). |
| A-15 | The per-currency payment default lands whole in the first PR (index, `DefaultForPaymentAsync(method, currency)`, clearing per currency, the one Host caller). | The index change alone breaks the old query the moment it lands. |
| A-16 | `/transactions`' either-leg wallet filter is `transactions.wallet_id = @id OR` a `transfers` leg on that wallet, not entries alone. | `Captured`/`Failed` records have no entries, and the filter shows them today. |
| A-17 | No live test reads a slip in this phase. | The live suite covers capture phrasings; drawing a slip would bring SkiaSharp into the Ai tests. |
| A-18 | *(Superseded for capture by A-27.)* A transfer leg the model leaves without a wallet goes to the cash default of its currency, else the currency's default — for capture and for slips alike. | "снял 10000 с райфа" belongs in Cash RSD; with the bank as the RSD default it would fail as `SameWallet`. Between banks the operator names both. |
| A-19 | A kept `Stated` charge stays when the foreign sum it priced changes; its rate is derived from the new sum and still marked `stated`. | Operator's call at the plan review. |
| A-20 | A fee said in exactly one side's currency is on that side, whatever leg the model named. | A menjačnica takes its commission in dinars on a EUR → RSD exchange; the default `from` leg would reject it as `InvalidFee`. |
| A-21 | *(Destination framing superseded by A-33.)* A transfer's correction input states each side as the operator would say it — the principal, plus the fee on that side as not included — and a side the ledger worked out as such, answered `null` unless the correction states it. A valid stated rate is kept even when both amounts are given. | Otherwise a date-only correction charges the fee twice and a rate-only correction is silently lost. |
| A-22 | *(Extended by A-31.)* Failure writes never overwrite a record that moved on: a failed first reading is marked `Failed` only while still `Captured`; Restore of a record a correction applied while it was cancelled restores `Completed`; `RecordExchange` leaves an already-applied record alone, so a reply queued during extraction wins. | Each was a race or ordering in which a booked record would leave the balances. |
| A-23 | A slip's printed issue date is the record's date until a correction says otherwise. | A slip photographed the next day, completed by a reply, would otherwise land on the capture day. |
| A-24 | Charges are never computed for a fiscal receipt's record (T-1); `DIN`/`ДИН` on a slip read as RSD; slip vision reports figures exactly as printed and never works one out; `/wallets` numeric fields take a comma as the decimal separator and refuse what they cannot parse. | The receipt echo cannot render a charge; Serbian slips print DIN; P6-2; MudBlazor's default converter reads `117,35` as 11735. |
| A-25 | The crossing-zero line compares the source's balance with and without this transfer under the checkpoint rule. | A backdated transfer absorbed by a later checkpoint did not cause today's negative balance. |
| A-26 | *(Wording amended by Phase 8a, IR-11: "neither `Failed` nor `Cancelled`" — Cancel keeps the reason; `p8a-1-integrity-and-bug-reports-decisions`.)* `RecordFailureReason` has `None = 0`, and a failure reason is never nullable: `transactions.failure_reason` is `not null default 0`, a record that is not `Failed` holds `None`, one `MarkFailedAsync(id, reason)` takes `None` for a failure with no named reason, and the echo's reason parameter defaults to `None`. | Operator's review of PR #32. |
| A-27 | Replaces A-18 for capture: a transfer's **source** leg the model leaves without a wallet goes to the **card** default of its currency, its **destination** leg to the **cash** default of its currency, each else the currency's default. A slip keeps cash on both legs (A-8, §3). | "снял 10000" with no wallet named would otherwise put both legs on the cash wallet and fail as `SameWallet`; a withdrawal takes from the card and lands in cash. Operator, 2026-10-01. |
| A-28 | The no-terms echo reads `{sum} {CUR} not converted — set a {CUR} rate for {Wallet} on /wallets, or correct this record to apply it` (`30.00 USD not converted — set a USD rate for Kaspi KZT on /wallets, or correct this record to apply it`), not §4's `not in the wallet's currency — set a USD rate for Kaspi KZT on /wallets`. | It leads with the amount, and the correction route keeps the hint true for a record saved before the rate existed, since rates are never applied retroactively (§1, "No backfill"). Operator, 2026-10-02. |
| A-29 | A slip is held for its amounts only when the dinar side differs from `foreign × rate`, after allowing for a printed commission, by **one dinar or more** beyond the error a rate printed to four decimals can carry (`foreign × 0.00005`): held when `\|unexplained\| ≥ 1 + foreign × 0.00005`. Replaces §3 Recording 3's "one para". The recorded amounts stay exactly as printed. | Offices pay out whole dinars (11 712 for 11 712.34), so a one-para band held nearly every real slip (closing review). The rate error is independent of that rounding and grows with the amount, so it stays on top. Operator, 2026-10-02. |
| A-30 | A slip whose currency was read but is not one the ledger holds is not called unreadable: `SlipIncomplete`'s echo reads `Slip read, but CHF isn't a currency this ledger holds — nothing recorded. If it was misread, reply with the right currency.` (`CHF and GBP aren't currencies this ledger holds` for two), ahead of any figure that is missing as well. | The code was read, not missed, and no reply can make the ledger hold it — only a misread can be corrected (closing review). |
| A-31 | Extends A-22: an incomplete slip saved while its photo was cancelled keeps `SlipIncomplete` and is restored `Failed`, so Restore asks for the missing figure. | Otherwise a slip cancelled during extraction and then restored was stranded, with nothing asking for what was missing (closing review). |
| A-32 | An amount said as what arrived ("получил", "пришло", "на руки") on the fee's side already includes the fee, so `included` is true unless the person says the fee was taken on top. Extends §2's *Prompt additions*, where `included` was true only when the operator said the amount includes it. | "получил 11700, комиссия 100 динар" stored the received leg as 11 600, though 11 700 is what arrived (closing review). |
| A-33 | A transfer's correction input states each side as the operator would say it: the source as handed over less its fee, with the fee beside it "not included in the figure" (answered `included: false`); the destination as what arrived (the stored amount), with its fee "already taken out of the figure" (answered `included: true`) — on the current record and on a slip's text alike. A side the ledger worked out is shown as such and answered `null` unless the correction states it. A new amount for a side keeps its fee's framing unless the correction says otherwise. | A destination shown before its fee contradicted the echo and capture's reading of "получил" as net (A-32), so a fee-only or bare-amount correction shifted what arrived by the fee. Closing review, 2026-10-02. |

## Changed by the closing review (2026-10-02)

Fable 5.1 and Codex reviewed `phase-7` against `master` at the close (`docs/REVIEWS.md`, trial tally).
Four findings changed the phase before it merged, each now an amendment above: the slip tolerance is
widened to one dinar, so a slip paid out in whole dinars is no longer held for "Record anyway" (A-29,
operator 2026-10-02); a slip in a currency the ledger does not hold is worded as an unsupported
currency, not an unreadable one (A-30, PR #56); Restore of an incomplete slip that was cancelled during
extraction brings it back `Failed` and asking for the missing figure instead of stranding it (A-31, PR
#56); and an amount said as what arrived includes a fee on its side, where the prompt had read it as not
included and stored the received leg short by the fee (A-32, PR #55); with it, a transfer's correction
input shows the destination as what arrived, its fee already taken out, so a fee-only or bare-amount
correction no longer shifts it by the fee (A-33, PR #55). Deferred: the opus review of the fixes found
the wider case A-31 leaves open — any job that fails while its record is cancelled leaves no reason, and
Restore brings the record back with no job and no buttons
(`docs/backlog/failed-job-on-a-cancelled-record.md`).

## Decisions made during implementation, worth knowing before revisiting this code

- **A reply to a slip wins over the photo's caption** (PR #48, extending A-8 and A-22). A held slip's
  "Record anyway" is refused while a reply to it is still pending or claimed, and saving a slip skips
  the caption's `Correct` job when a reply arrived while the photo was being read — otherwise the older
  caption, queued behind the reply, re-applied its own figures over the operator's. A reply's job
  (a typed reply's `Correct`, a voice reply's `Transcribe` and the `Correct` it leads to) takes the
  record's row lock before it is stamped and inserted, so it is ordered wholly before or after a slip
  save or a "Record anyway" press (`EfReceiptStore.HasReplyInFlightAsync`, `EfRecordEditor`,
  `EfTranscriptionStore`).
- **Whether a transfer's received amount was said or worked out is not stored** (Codex review of
  PR #32, operator's choice): the correction input infers it from the arithmetic (A-21), and the cases it
  cannot tell apart are backlog, `docs/backlog/transfer-amount-provenance.md`, rather than a column in
  this phase's migration.
- **Foreign-currency income is still not converted.** Charges are computed for `Expense` records only
  (spec §1, *Posting*: Income unchanged), so income in a currency other than its wallet's keeps M10's
  separate currency line and the echo's "no conversion yet" — as does a spending in a currency the
  wallet has no terms for, whose echo says how to set them.
- **Parked by the operator at the phase's close (2026-10-02):** the trace page's rendering of an
  exchange slip (`docs/backlog/trace-page-exchange-slip.md`), the trace page never showing a failure
  reason (`docs/backlog/trace-page-failure-reason.md`), and the two-commit failure path every worker
  shares (`docs/backlog/atomic-job-and-record-failure.md`).
