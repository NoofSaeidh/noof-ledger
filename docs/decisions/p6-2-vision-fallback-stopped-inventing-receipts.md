---
id: P6-2
title: "Vision fallback stopped inventing receipts (2026-09-27)"
status: decided
date: 2026-09-27
phase: Phase 6
related: [p6-1-receipts-decisions, p2-1-quote-and-verify-removed]
---

Production evidence, 2026-09-27: Telegram-compressed receipt photos (and even files) reaching the vision
fallback (`read_receipt` on Haiku) produced whole invented receipts — a real 4-line 1570.96 RSD cash
receipt came back as "BISIBONSKA ŠTAMPA 110 RSD, 1 line", again as "МИНИСТЕРЕЛНИ 110 RSD" with an invented
PIB, another as "MAXI HOLDING 2322 RSD, 16 lines" whose lines summed to 7664, and one took the capture's
own location line for the PIB and the capture time for the issue time — every one recorded as an expense.
Duplicates went uncaught because vision returned no fiscal number at all. The operator's five decisions,
taken before implementation, all settled — the model stays Haiku (a separate model, and choosing one in
the UI, is deferred: `docs/backlog/deferred-from-phase-6-receipts.md`):

1. **Do not invent.** `read_receipt` lets every field come back null when it is not legible, adds
   `readable`/`unreadable_reason` (`too_small`, `blurry`, `not_a_receipt`, `cut_off`, `other`) and a
   nullable `total`. `IReceiptVision.ReadAsync` returns `ReceiptVisionResult` (shaped like
   `FiscalFetchResult`) rather than a bare `ExtractedReceipt`, so an unreadable photo is a distinct,
   honest outcome, not a half-built record. Two deliberate defaults are kept from before, and they are
   not the same kind of default: **currency** null → `RSD`, since Serbian fiscal receipts print RSD
   and a legible receipt showing no other currency is one — this one is a read fact, not a guess.
   **Kind** is different: `ExtractedReceipt.Kind` has no null to hold, so an unclear sale-or-refund
   still defaults to `Sale`, but that default is never trusted silently — `ChatReceiptVision` also
   reports `KindUnclear`, and `ExtractReceiptWorker` treats it exactly like a mismatch or a malformed
   tax id (item 2): saved so the echo shows what was read, held back from `CategorizeReceipt` until
   "Record anyway", *unless* a decoded QR's own kind resolves it first (item 6) — a guessed `Sale` on
   an actual refund would otherwise change the money's direction with no operator check. Every other
   field must be read, never guessed.
2. **Do not record when it does not add up — vision receipts only, never a fiscal QR/SUF receipt.** A
   receipt whose lines do not sum to its total beyond a cent, or whose printed tax id is not exactly 9
   digits, is still saved (so the echo shows exactly what was read) but `CategorizeReceipt` is not
   enqueued until the operator presses the new "Record anyway" button (`RecordAction.RecordAnyway`),
   which reuses the transaction's own `SourceMessageId` unique index for idempotency — the same pattern
   `EfRecordEditor` already uses for a redelivered correction.
3. **Large files.** `IReceiptImageScaler` (`SkiaReceiptImageScaler`, SkiaSharp) downscales a photo to a
   1568px long side and re-encodes it as JPEG immediately before the vision call; the QR reader keeps
   reading a capture's original bytes.
4. **Duplicates.** `read_receipt` also returns the printed fiscal number and PIB; `ChatReceiptVision`
   accepts either into `ExtractedReceipt` only when it is well-formed (PIB `^\d{9}$`, fiscal number
   `^[A-Z0-9]{8}-[A-Z0-9]{8}-\d+$`), so the existing `(seller_tax_id, fiscal_number)` duplicate index
   covers vision receipts too without risking a false match on OCR noise. Never applied to a fiscal
   QR/SUF receipt.
5. **Echo hint.** Any vision-read receipt's echo carries one standing line suggesting the QR link,
   scanned by the phone's own camera, for an exact read next time — no new column, computed from
   `ReceiptSource.Vision` alone. Not a file: a photo, even a full-resolution one sent as a file, does not
   reliably decode the fiscal QR either (the operator's own real-photo evidence, Phase 6 QR entry in
   `p6-1-receipts-decisions.md`) — only the link does.
6. **Every fact a decoded QR carries wins over vision, not just Total/Currency** (Copilot finding on
   PR #3, closed 2026-09-27): when the QR decodes but the Tax Administration fetch fails, `ExtractReceiptWorker`
   also overwrites the vision answer's `IssuedAt`, `Kind` and `FiscalNumber` (composed as
   `{RequestedBy}-{SignedBy}-{TotalCounter}`, `FiscalQrPayload.FiscalNumber`) with the QR's own — the
   seller PIB has no QR equivalent and stays vision's well-formed-only value, and a non-money QR kind
   (Copy/Training/Proforma/Advance) then takes the existing non-money path since the saved `Kind` is
   what routes it.

**The malformed-tax-id / unclear-kind residual.** A stored `receipts` row never carries a malformed PIB
— `ChatReceiptVision` drops it to `null` before it is ever saved — and never carries "kind unclear"
either, since `Receipt.Kind` is already resolved to the concrete `Sale` default by the time it is saved.
So a replay of a still-unconfirmed job (`ExtractReceiptWorker`'s own C-1 path) or the operator's
Cancel/Restore of one (`RecordActionHandler`) cannot recover *why* it is awaiting confirmation when
either was the only reason: the confirmation prompt reappears with no listed problem, just "This
receipt doesn't look right … Record it anyway, or cancel?" The receipt's own lines and total are still
shown in full, so for the malformed-PIB case the operator can still judge it against the paper in hand.
The unclear-kind case is worse: nothing about a stored `Receipt` distinguishes a read `Sale` from a
guessed one, so after a Restore the operator sees the same plain prompt and "Record anyway" can book a
`Sale` that was in fact a coin-flip on a refund. Accepted rather than fixed for both, since fixing either
would mean persisting a value — the raw malformed PIB, or a "kind was guessed" flag — that is otherwise
never stored anywhere, for a rendering-only purpose.

**P2-1 is unchanged by any of this.** Text and voice capture still have no validation layer — the model
interprets amounts and dates from natural speech, and the safety is the echo plus cancel/correct, exactly
as P2-1 settled. These five decisions are additions to the *fiscal-receipt* pipeline specifically (R-6's
"the model only categorises/reads photos" boundary), not a reversal of the capture-is-the-exception rule.

**Addendum, 2026-09-27 (Copilot finding on PR #3):** when the QR decodes but the Tax Administration
fetch fails, `ExtractReceiptWorker` now normalizes a Vision-sourced receipt's `Total` to its verified
`QrTotal` before the mismatch check, the save and the echo, so a model total that disagreed with the QR
can no longer reach `Receipt.Total` or be shown as it.
