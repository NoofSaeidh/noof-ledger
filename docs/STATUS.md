# Status

> **Status:** spec approved (`docs/specs/2026-09-24-money-model.md`,
> `docs/specs/2026-09-24-observability-design.md`, `docs/specs/2026-09-25-receipts-design.md`,
> `docs/specs/2026-10-01-transfers-and-exchange-design.md`,
> `docs/specs/2026-10-02-integrity-and-bug-reports-design.md`); **Phases 0, 0b, 1A, 1B, 1C, 1D, 2, 3, 4,
> 5, 6, 7 and 8a complete** — solution, EF Core model and migrations, PostgreSQL money-storage gate,
> cookie authentication as the sole mode, the `user set-password` verb, the loopback interlock, a Blazor
> Server shell, Telegram capture with a durable queue, natural-language capture, voice notes transcribed
> by Groq's whisper-large-v3, the money model (every wallet's balance — opening balance, minus spending,
> plus income, plus or minus the transfers that move money between wallets, re-anchored by the
> operator's own balance statements — is exact in all five currencies (EUR, RSD, USD, RUB, KZT) under
> `ru-RU` and `sr-Latn-RS`), observability: the host waits indefinitely for PostgreSQL instead of
> exiting when it is down, every page shows a waiting banner until the database gate is `Ready`, Serilog
> logs to a rolling file and to the `app_log` table with secrets redacted before every sink, a
> transaction's whole path from message to echo (and its revision history) is visible on one trace page,
> and system health is on a dashboard tile, on `/diagnostics` and behind the bot's owner-only `/health`
> command. Health checks are now our own `ISystemHealthCheck` seam (no
> `Microsoft.Extensions.Diagnostics.HealthChecks`), each owned and registered by the assembly that owns
> what it checks, run one at a time under a 5 s cooperative timeout; a check that throws or times out
> shows only its exception type. Every model, speech, Telegram, worker and health-check call is timed
> through `IOperationTimer`, logged as Debug or, over its own slow threshold
> (`Logging:SlowOperationMs`), a Warning. The database log sink's minimum level is runtime state — any
> of the six severities, or Off — switched on `/diagnostics/logs/settings` (linked from the Logs page
> header) and persisted in `app_setting`, alongside per-level database retention days there too; while
> the file and console sinks each keep their own static floor, `Logging:File:MinimumLevel` and
> `Logging:Console:MinimumLevel`. Above Information (or Off) the trace page says so with an inline
> notice, since the database sink then drops the stage events it reads; a transaction's trace page links
> straight into the Logs page at Debug for that transaction. And now receipts: a photographed Serbian
> fiscal receipt, or its QR link sent as text, decodes offline and pulls its lines from the Tax
> Administration; with no readable QR, or when the Tax Administration is unreachable, a vision model
> reads the photo instead and the echo carries a visible warning (and the Receipts health check turns
> amber for 24 h). Every receipt line becomes its own categorised line item, in the receipt's own order;
> the shop is a merchant row keyed by its tax id, so the same shop next time costs no merchant tokens.
> Transactions carry a `Kind` (`Expense`/`Income`/`BalanceCheck`/`Transfer`); expense, income and
> transfer transactions own signed double-entry-lite `entries`; a balance statement is a
> `balance_checks` checkpoint; a wallet's balance is computed by the `wallet_balances` SQL view and read
> back by `IBalanceReadModel`, never stored. The model's answer tool is `record_transaction` (was
> `record_spending`) and now names a wallet and, for a balance statement, the stated amount, or for a
> transfer both legs. `/wallets` manages wallets and each one's card/cash payment default; income and
> balance statements are ordinary messages to the bot. And now money that moves between the operator's
> own wallets: a withdrawal, a top-up, a transfer between banks or a currency exchange is one `Transfer`
> with both legs in a `transfers` row, each leg in its wallet's own currency and holding what that
> wallet actually moved; a fee is a `Fees & Charges` line item on the leg that paid it, the only part of
> a transfer that counts as spending. The model copies the amounts, rate and fee as said and C# does the
> arithmetic; an exchange told without the amount received or a rate is not recorded, and the bot asks
> for it. A spending in a currency other than its wallet's is charged to the wallet at that wallet's own
> terms for the currency — a rate and a percentage, fixed or minimum fee, set on `/wallets` — or at the
> charge the operator states, and the charge is frozen with the record, so changing the terms never
> reprices history. A photographed exchange-office slip is read by vision and recorded as an exchange
> between the cash wallets of its two currencies, with the office as the venue; it is evidence the
> operator corrects like a spoken amount (unlike a fiscal receipt), a duplicate slip is caught by its
> number, a slip whose figures disagree waits for "Record anyway", and one with an unreadable amount
> asks for it. Card and cash payment defaults are per currency. Home has two lenses: *This month* shows
> spending and income (a new *Received* line) without transfer principals, and *Transfers this month*
> lists the month's transfers and exchanges with the fees they cost; the *Recent* list switches between
> *Spending & income*, *Transfers* and *All* and marks each row's kind. `/transactions` can hide
> transfers (a checkbox, or `?view=spending` / `?view=transfers`) and its wallet filter matches either
> leg; the trace page shows a transfer's legs, fee, rate and venue, and a foreign spending's charge. And
> now the app checks itself: four integrity checks run on demand — on `/diagnostics/integrity`, behind
> the health tile and `/health` — and every row they return is a finding, kept in two groups. *Bugs* are
> data our own code wrote that contradicts itself — ledger entries that disagree with the lines, charges
> and transfer legs they are summed from, a record whose stored facts contradict its kind, a message
> captured with nothing working on it — and turn the tile red; *Waiting on you* is a record that has
> waited more than a day on the operator — a reply the bot asked for, "Record anyway", a correction that
> never applied, a record restored after a cancel — and turns it amber. Nothing about a finding is
> stored: it disappears when the data or the code is fixed. A model explains a finding on request and,
> when it judges the cause to be the app, offers to file a bug report; the operator also files one from
> the bot with `/bug` (a reply to an echo links the record), and the bot answers with an explanation. A
> report keeps the findings, the log lines and the record as they were when it was filed; `/bugs` lists
> them, and the open ones reach Claude Code as Markdown — downloaded from `/bugs`, written by `.\run.ps1
> bugs export`, or triaged by the `/bugs` skill. The app also backs itself up daily — `pg_dump -Fc` into
> `%LOCALAPPDATA%\NoofLedger\backups`, the newest 14 kept, every run logged to `backup_runs` — and
> `ops/restore-check.ps1` proves a dump restores to the same balances, checked so far against a template
> clone; the one check against the live ledger itself is the operator's to run (`ops/RUNBOOK.md`). The
> full `dotnet test --solution` suite passes, with the live-only tests skipped, apart from one known
> timing race — `DatabaseLogLevelDbTests`' startup burst
> (`docs/backlog/loose-ends-phase-5-observability.md`) — which fails on every local `.\run.ps1 test db`
> (CI does not run it); the Playwright browser tests are in the solution now, so `dotnet test
> --solution` runs them too and needs Chromium present. Live suites stay skipped unless
> `NOOF_LEDGER_LIVE_ANTHROPIC_KEY` / `NOOF_LEDGER_LIVE_GROQ_KEY` + `NOOF_LEDGER_LIVE_VOICE_FILE` are
> set; `ops/publish.ps1` produces a runnable host. **`run.ps1` in the repo root is the one entry point
> for launching and operating the app** (`.\run.ps1 help`) — `dotnet run` and the published exe now
> behave the same. Spending analysis (Phase 8b), editing receipt lines, and correcting the amounts of a
> vision-read fiscal receipt remain future phases. `.\run.ps1 demo` runs the app on a mock-data database
> with nothing to configure, and `.\run.ps1 screenshots` keeps `docs/screenshots/` current. Known and
> open: `.\run.ps1 test db -Filter` filters only the Persistence tests, so a Host or Demo class name
> stops the run before Host is reached (`docs/backlog/run-test-db-filter-reaches-only-persistence.md`);
> and the `bugs export` verb, run with `DOTNET_ENVIRONMENT=Development` set, crashes with a stack trace
> instead of printing its one line — `run.ps1` passes `--no-launch-profile`, so only a shell with that
> variable set hits it, and whether the verb should run under Development is a question for the
> operator.
