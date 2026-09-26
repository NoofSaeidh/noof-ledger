---
paths:
  - "src/Noof.Ledger.Telegram/**"
  - "tests/Noof.Ledger.Telegram.Tests/**"
  - "src/Noof.Ledger.Application/Chat/**"
  - "src/Noof.Ledger.Host/Workers/CategorizationWorker*.cs"
  - "src/Noof.Ledger.Host/Workers/TranscriptionWorker*.cs"
---

## CLAUDE.md §4 — Bot text *(settled 2026-09-23)*

- The bot writes English only, including the category name shown in the echo (`Category.NameEn`).
  Multi-language is deferred — `docs/BACKLOG.md`. The operator may still write to the bot in any
  language; only the bot's own output is English.
- Identifiers and comments use English action names — `Cancel`/`Edit`/`Restore` — never the Russian
  labels the UI used to show.
