---
paths:
  - "src/Noof.Ledger.Receipts/**"
  - "tests/Noof.Ledger.Receipts.Tests/**"
  - "src/Noof.Ledger.Application/Receipts/**"
  - "src/Noof.Ledger.Persistence/Receipts/**"
  - "src/Noof.Ledger.Host/Workers/ExtractReceiptWorker*.cs"
  - "src/Noof.Ledger.Host/Workers/ReceiptCategorizationWorker*.cs"
  - "src/Noof.Ledger.Host/Workers/CategorizationWorker*.cs"
  - "src/Noof.Ledger.Ai/*Receipt*.cs"
  - "tests/Noof.Ledger.Ai.Tests/**/*Receipt*.cs"
  - "tests/Noof.Ledger.Host.Tests/*Receipt*.cs"
  - "src/Noof.Ledger.Telegram/CorrectionHandler.cs"
  - "src/Noof.Ledger.Telegram/TelegramUpdateRouter*.cs"
---

## CLAUDE.md §4 — Money: receipts

- **A receipt's amounts and date never come from the model** *(settled 2026-09-25, Phase 6)*. The Tax
  Administration's fiscal QR, or the vision fallback, produces `receipts`/`receipt_lines`; the model
  only assigns a category per line and, on first sighting, the merchant's canonical name — C# builds
  every `Money` and the transaction's `OccurredOn` from those rows, never from the categoriser's
  answer. Every correction on a transaction that has a receipt is routed, at claim time, to the receipt
  path rather than `record_transaction` — a typed reply, a voice reply, or an edited message all hit
  the same one check (`CategorizationWorker.TryRouteToReceiptAsync`), so a correction can change the
  category or the wallet but never the amounts a fiscal receipt or the Tax Administration already
  fixed.
