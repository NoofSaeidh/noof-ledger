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
- **The vision fallback must never invent what it cannot read — settled 2026-09-27.** `read_receipt`
  reports `readable`/`unreadable_reason` and a nullable `total` rather than guessing; a receipt whose
  lines do not sum to its total (beyond a cent) — the QR total when one decoded, since a vision total
  that disagrees with a verified QR total is normalized to it before this check ever runs — or whose
  printed tax id is not exactly 9 digits is saved
  but held back from `CategorizeReceipt` until the operator presses "Record anyway"
  (`RecordAction.RecordAnyway`) — never for a fiscal QR/SUF receipt, whose own numbers are trusted as
  before. A printed PIB/fiscal number is accepted into `ExtractedReceipt` only when well-formed. Full
  reasoning and the production evidence that prompted it: `docs/OPEN-QUESTIONS.md` P6-2.
