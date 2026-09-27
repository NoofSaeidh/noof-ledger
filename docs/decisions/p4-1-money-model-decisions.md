---
id: P4-1
title: "Phase 4 money model decisions (2026-09-24)"
status: decided
date: 2026-09-24
phase: Phase 4
---

The operator made these decisions during Phase 4 spec discussion; the rules themselves live in
`CLAUDE.md` and `docs/specs/2026-09-24-money-model.md` (M1–M13, B1–B5). Recorded here for
the reasoning.

| # | Decision | Reasoning |
|---|---|---|
| M1 | A wallet's balance is derived from entries and checkpoints, never stored as a column | A cached balance drifts from what produced it the moment anything writes around it instead of through it; deriving it is the only way "exact" stays true after the code that computed it once is forgotten |
| M3/M4 | `transactions.wallet_id` becomes nullable; the wallet is chosen when the record is read (the model names it, or the currency's default), not at capture | Capture no longer needs a wallet at all, and "no default wallet" stops being a capture-time failure — it only matters once the model has actually named a currency |
| M6 | A balance statement is a checkpoint (`balance_checks`), not an adjustment entry | An adjustment entry would hide the operator's own correction inside the same ledger as ordinary spending; a checkpoint keeps it as what it is — a fact about reality overriding the app's own running total — and gives the dashboard a "last reconciled" date for free |
| M10 | Cross-currency spending is not converted; it is recorded in its own currency, shown as a separate balance line | *"курсы зависят от банка — отложи"* (operator). Inventing a rate the bank would not actually use is worse than an honest, visible second currency line — `docs/BACKLOG.md` records the deferred conversion work |
| B2 | An external process's secret goes through its environment only, never an argument | `Process.Start` arguments are visible to anything that can list processes (Task Manager, `Get-Process`, a crash dump); an environment variable set via `ProcessStartInfo.Environment` is not |
| B5 | `restore-check.ps1` runs against a real database only once, by the operator's own hand, with explicit permission | The script legitimately reads `noof_ledger` to compare against a restored scratch copy — never writes to or drops it — but "this script only ever reads the real ledger" is a claim worth the operator's own eyes once, not something Phase 4's automated tests should assert on his behalf against his real data |
