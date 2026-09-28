---
id: S-1
title: "Demo database and screenshots (2026-09-26, operator decisions)"
status: decided
date: 2026-09-26
related: [demo-cut-from-first-design]
---

| # | Decision | Reasoning |
|---|---|---|
| (a) | A long-lived `noof_ledger_demo`, refreshed with mock data on every run, runs the real app with nothing to configure (`.\run.ps1 demo`); screenshots are taken from it | The operator wants to see new functionality without configuring anything, and to take screenshots from the same data |
| (b) | Mocks are for pictures, not behaviour: fake AI keys and a fake backup row; the bot's replies are drawn from the app's own reply text. No working fake Telegram, no fake model | A first design with a live fake chat and a rule-based model was cut back by the operator as more than the goal needs (`docs/backlog/demo-cut-from-first-design.md`) |
| (c) | Screenshots are committed under `docs/screenshots/` and shown in `README.md`; git history is the before/after | To see how the app looks and track changes in the repo itself |
| (d) | A change to anything the operator sees regenerates them, commits the changed images with the change, and sends them to the operator (`CLAUDE.md` §5) | So every visible change reaches the operator's phone |
| (e) | The mock data is dated in the current month, not a fixed one *(implementation ruling, 2026-09-27)* | The dashboard's "This month" follows the real calendar; fixed dates would have emptied its charts from the next month on. The cost: date-bearing pictures change once a month |
| (f) | The Logs picture shows only generic rows from the last three days of the previous month, not the receipts' and traces' stage rows *(final-review ruling, 2026-09-28)* | On days 1–20 of a month the host's own startup rows (real now, including a machine path) fell inside the old window and rewrote the picture on every run. The cost: a thinner Logs picture; the trace pictures still show the stage rows |
