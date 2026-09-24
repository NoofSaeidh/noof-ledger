# Money model and a restored backup — design

**Status:** approved by the operator 2026-09-24.
This is Phase 4 in the phase table of `2026-09-22-natural-language-capture.md`. It amends
`2026-09-19-noof-finance-design.md` §6 (ledger and wallets) where noted.

## Why this exists

The tracker records spending but cannot say how much money there is. The phase criterion is the one
the original design set: **balances exact across all five currencies under `ru-RU` and `sr-Latn-RS`,
and a backup restored successfully at least once.** An untested backup is a hypothesis; a balance
that drifts from the bank is worse than none.

It has two independent parts, built as two tracks of one plan: **4A** the money model, **4B** backup
and a verified restore.

## Decisions — 4A, the money model

**M1 — A balance matches the bank.** *(Operator, 2026-09-24.)* Every wallet's balance is what the
bank or the purse would show: a starting balance, minus spending, plus income, corrected by the
operator's own balance statements. It is not a spending total.

**M2 — Wallets are managed on the dashboard; income and balance statements are told to the bot.**
*(Operator.)* A new `/wallets` page creates a wallet (name, currency, opening balance and its date,
alias words, "default for its currency"), renames it, edits its aliases and archives it. Income
(*"пришла зарплата 2000 евро на Wise"*) and balance statements (*"на райфе 45 тысяч"*) are ordinary
messages to the bot, typed or spoken.

**M3 — The model picks the wallet from how the operator speaks.** *(Operator.)* The request offers
the model the active wallets (id, name, currency, aliases), as it offers categories. The answer names
one wallet id or none. None → the default wallet of the spending's currency; no such wallet → the
default wallet of `Categorization:DefaultCurrency` (RSD). The choice is mapped, never validated
(P2-1). A correction (*"это было с налички"*) moves a record to another wallet.

**M4 — Wallet.** Name, currency, aliases (a list of words), `IsDefaultForCurrency` (at most one per
currency, a partial unique index), `Archived` (hidden from capture, history kept), `CreatedAt`. The
single global `IsDefault` becomes per-currency: the seeded *Main Wallet* is the RSD default. No
`Account` table: *"Wise EUR"* and *"Wise USD"* are two wallets whose names say so (§6's Account ×
Currency collapses to a naming convention until something needs the grouping).

**M5 — Double-entry-lite: transactions own signed entries.** *(Operator, approach A; matches §6.)* A
transaction has a `Kind`: `Expense = 0`, `Income = 1`, `BalanceCheck = 2` (`Transfer = 3` reserved for
Phase 7). Expense and income transactions own **entries** — wallet, signed amount `numeric(19,4)`,
currency, role (`Principal`; `Fee` reserved for Phase 7). An expense's entries are minus the sum of its
lines per currency; an income's are plus. Entries are rewritten in the same database transaction that
rewrites the lines, and an integrity test asserts they agree. `LineItem` stays the categorised
breakdown. Phase 7 transfers become two entries with no redesign.

**M6 — A balance statement is a checkpoint, not an adjustment entry.** The operator's *"на райфе 45
тысяч"* becomes a `BalanceCheck` transaction with one `balance_checks` row: wallet, currency, the
stated amount, and — for the echo and the history — the balance the app computed just before it. **A
wallet's balance in a currency = the stated amount of its latest checkpoint + the sum of its entries
after that checkpoint** (no checkpoint → the sum of all its entries). "After" orders by
`(occurred_on, occurred_at)` — the purchase's own day first, then its send instant — so a note sent
at 9:00 but transcribed after a 9:02 statement still falls before it, and *"вчера купил…"* told after
today's statement still falls before it. Processing order never changes a balance. The difference a
statement reveals (*"adjusted +200"*) is derived and shown, never stored as money. A zero difference
is recorded too: it is the "last reconciled" date.

**M7 — The opening balance is the first checkpoint.** Creating a wallet with an opening balance
writes a `BalanceCheck` transaction dated as the operator says. Such dashboard-made transactions have
no Telegram message: `capture_kind` gains `Manual = 2`, and `telegram_chat_id`/`telegram_message_id`
become nullable (the unique index filters out nulls; a check constraint requires both for `Text`/`Voice`
and neither for `Manual`).

**M8 — Income has its own categories.** A seeded `income` parent with `salary`, `refund`, `gift` and
`other-income`. The model is told which subtree fits which kind; C# maps slugs as today and does not
police the pairing (P2-1).

**M9 — The answer tool becomes `record_transaction`.** `record_spending` is renamed — it now records
income and balance statements too — and gains `kind` (`expense` | `income` | `balance`), `wallet`
(an offered wallet id or null) and `balance` (`{amount, currency}` or null, for `kind = balance`).
Still a forced strict tool call, every property required with nullable types, amounts as JSON numbers
read straight into `decimal` (CLAUDE.md §4 *The model*). Prompt tuning waits for Phase 11.

**M10 — A spending in a currency other than its wallet's is not converted (deferred).** *(Operator:
"курсы зависят от банка — отложи".)* Its entry keeps the spending's currency, so the wallet's balance
shows it as a separate currency line — visible, exact, and not invented. The echo adds *"not in the
wallet's currency — no conversion yet"*. `docs/BACKLOG.md` records the conversion work.

**M11 — What the operator sees.**
- Echo, expense: `Recorded — Raiffeisen RSD · balance 45 230.00 RSD`; income: `Income — Wise EUR ·
  balance 3 200.00 EUR`; statement: `Raiffeisen RSD: balance was 44 800.00, you said 45 000.00 —
  adjusted +200.00`. Balances are read from the database after the write and rendered by C#.
  Cancel and Restore re-render the balance.
- Dashboard home: a **Balances** card — each active wallet, its balance per currency and its last
  reconciliation date, and a per-currency total. No cross-currency sum. "This month" spending counts
  `Expense` only.
- `/wallets`: the management page (M2). Inline `MudAlert` feedback only — no popover, dialog,
  snackbar, tooltip or menu (CLAUDE.md §4 *Render modes*).

**M12 — Exactness is proven under both cultures.** Sums are `numeric(19,4)` in SQL and `decimal` in
C#; everything shown is formatted with `InvariantCulture`. An end-to-end test drives capture →
entries → balance → echo and dashboard for all five currencies (large KZT values, EUR cents) and runs
twice, under `ru-RU` and `sr-Latn-RS`, against literal expected values. A test that reads what the
dashboard serves asserts the balance appears as exactly that figure.

**M13 — Migration.** One additive migration: wallet columns (the global default becomes the RSD
default); transaction `kind` (default `Expense`), `capture_kind` `Manual`, nullable Telegram ids with
the new constraints; `entries`; `balance_checks`; the income categories; and a backfill writing one
entry per currency for every existing `Completed` expense. Revision snapshots gain `kind`, `wallet_id`
and the stated balance.

## Decisions — 4B, backup and a verified restore

**B1 — The app backs itself up daily.** *(Operator.)* A `BackupWorker` runs at start and then every
24 hours; when the newest successful backup is older than 24 hours it runs `pg_dump -Fc` into
`%LOCALAPPDATA%\NoofLedger\backups\noof_ledger-yyyyMMdd-HHmmss.dump`, writing under a temporary name
and renaming on success so a half-written dump never looks finished. The newest 14 are kept.
`Backup:PgDumpPath` defaults to `C:\Program Files\PostgreSQL\18\bin\pg_dump.exe`.

**B2 — The password never leaves the child process's environment.** Connection parameters reach
`pg_dump` through `PG*` environment variables of that process only — never its arguments, a log line
or an error message. A test with a marker password proves it.

**B3 — Every run is recorded and visible.** A `backup_runs` table keeps start, finish, outcome, file
name, size and error text. The dashboard shows *Last backup: 3 h ago*, amber when it is older than 36
hours or the last run failed.

**B4 — A restore is checked, not assumed.** `ops/restore-check.ps1` restores the newest dump with
`pg_restore` into a scratch database, compares per-table row counts and every wallet's balances with
the live database (read-only), prints the result and drops the scratch database. An automated test
does the same round trip on a clone of the test template.

**B5 — The phase closes on a real restore.** `restore-check` runs once against the real database —
it reads `noof_ledger`, so only with the operator's explicit permission, or the operator runs it. The
result is recorded in `ops/RUNBOOK.md` and `README.md`.

## Known limits, recorded rather than hidden

- A purchase dated to the same day as a checkpoint and sent after it counts as after it (M6's
  ordering) — the next statement absorbs it.
- Cross-currency spending is not converted (M10).
- Dumps are not encrypted; they sit in the user profile with the database's own protection. OneDrive
  is Phase 10 (Q8: dump only).

## Testing

TDD as always. Persistence tests on template clones for entries, checkpoints, the balance query and
its ordering, the migration's backfill and constraints; worker and echo tests with fakes; the
culture-pair end-to-end test (M12); the backup worker with a fake process runner plus one real
round-trip test using the installed `pg_dump`/`pg_restore`; E2E for `/wallets` and the Balances card.
No test touches `noof_ledger`, the live model or the network.

## Documentation that closes the phase

`CLAUDE.md` (status; one rule: balances are derived from entries and checkpoints, never stored),
`README.md` (balances and the restored backup leave the "still missing" list only when true),
`docs/OPEN-QUESTIONS.md` P4-1 (these decisions and the operator's words), `docs/BACKLOG.md`
(cross-currency conversion, dump encryption, the same-day checkpoint ordering), `ops/RUNBOOK.md`
(backups: where, how many, how to restore, the restore-check record).

## Out of scope

Transfers and currency exchange (Phase 7), fees, cross-currency conversion, receipt photos, an
`Account` table, budgets, OneDrive, encrypted dumps.
