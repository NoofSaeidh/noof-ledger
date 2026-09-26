# Status

> **Status:** spec approved (`docs/superpowers/specs/2026-09-24-money-model.md`,
> `docs/superpowers/specs/2026-09-24-observability-design.md`); **Phases 0, 0b, 1A, 1B, 1C, 1D, 2, 3, 4
> and 5 complete** — solution, EF Core model and migrations, PostgreSQL money-storage gate, cookie
> authentication as the sole mode, the `user set-password` verb, the loopback interlock, a Blazor
> Server shell, Telegram capture with a durable queue, natural-language capture, voice notes
> transcribed by Groq's whisper-large-v3, the money model (every wallet's balance — opening balance,
> minus spending, plus income, re-anchored by the operator's own balance statements — is exact in all
> five currencies (EUR, RSD, USD, RUB, KZT) under `ru-RU` and `sr-Latn-RS`), and now observability: the
> host waits indefinitely for PostgreSQL instead of exiting when it is down, every page shows a waiting
> banner until the database gate is `Ready`, Serilog logs to a rolling file and to the `app_log` table
> with secrets redacted before every sink, a transaction's whole path from message to echo (and its
> revision history) is visible on one trace page, and system health is on a dashboard tile, on
> `/diagnostics` and behind the bot's owner-only `/health` command. Health checks are now our own
> `ISystemHealthCheck` seam (no `Microsoft.Extensions.Diagnostics.HealthChecks`), each owned and
> registered by the assembly that owns what it checks, run one at a time under a 5 s cooperative
> timeout; a check that throws or times out shows only its exception type. Every model, speech,
> Telegram, worker and health-check call is timed through `IOperationTimer`, logged as Debug or, over
> its own slow threshold (`Logging:SlowOperationMs`), a Warning. The database log sink's minimum level
> is runtime state — any of the six severities, or Off — switched on `/diagnostics/logs/settings`
> (linked from the Logs page header) and persisted in `app_setting`, alongside per-level database
> retention days there too; while the file and console sinks each keep their own static floor,
> `Logging:File:MinimumLevel` and `Logging:Console:MinimumLevel`. Above Information (or Off) the
> trace page says so with an inline notice, since the database sink then drops the stage events it
> reads; a transaction's trace page links straight into the Logs page at Debug for that transaction.
> Transactions carry a `Kind`
> (`Expense`/`Income`/`BalanceCheck`); expense and income transactions own signed double-entry-lite
> `entries`; a balance statement is a `balance_checks` checkpoint; a wallet's balance is computed by the
> `wallet_balances` SQL view and read back by `IBalanceReadModel`, never stored. The model's answer tool
> is `record_transaction` (was `record_spending`) and now names a wallet and, for a balance statement,
> the stated amount. `/wallets` manages wallets on the dashboard; income and balance statements are
> ordinary messages to the bot. The app also backs itself up daily — `pg_dump -Fc` into
> `%LOCALAPPDATA%\NoofLedger\backups`, the newest 14 kept, every run logged to `backup_runs` — and
> `ops/restore-check.ps1` proves a dump restores to the same balances, checked so far against a
> template clone; the one check against the live ledger itself is the operator's to run
> (`ops/RUNBOOK.md`). **1306 solution tests — 1295 passing, 11 live-only tests skipped, none failing** —
> the Playwright browser tests are in the solution now, so `dotnet test --solution` runs them too and
> needs Chromium present. Live suites stay skipped unless `NOOF_LEDGER_LIVE_ANTHROPIC_KEY` /
> `NOOF_LEDGER_LIVE_GROQ_KEY` + `NOOF_LEDGER_LIVE_VOICE_FILE` are set; `ops/publish.ps1` produces a
> runnable host. **`run.ps1` in the repo root is the one entry point for launching and operating the
> app** (`.\run.ps1 help`) — `dotnet run` and the published exe now behave the same. Cross-currency
> conversion, transfers and receipt photos remain future phases.
