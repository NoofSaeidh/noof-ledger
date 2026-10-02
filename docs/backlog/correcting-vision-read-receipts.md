---
title: Correcting the amounts of a receipt the vision fallback read
status: deferred
area: other
since: 2026-10-01 (Phase 7 brainstorming)
related: [deferred-from-phase-6-receipts]
---
**Operator, 2026-10-01:** vision often reads badly, so "a receipt's amounts never come from the model"
cannot mean its amounts can never be corrected. Scheduled as its own phase (or the first candidate
after Phase 7), not built inside Phase 7.

**Today.** `CategorizationWorker.TryRouteToReceiptAsync` routes every correction on a transaction with
a money receipt to the receipt path, whatever produced the receipt's amounts — the Tax
Administration's data from a fiscal QR, or the vision fallback. A vision read is guarded only by the
"Record anyway" hold (lines that do not sum to the total, a malformed PIB); once recorded, a wrong line
amount, total or date cannot be changed by a reply. In practice this covers most receipts:
`deferred-from-phase-6-receipts.md` records that no real fiscal QR decoded from the operator's photos.

**The intended boundary.** Locked is only what a fiscal QR confirmed — the Tax Administration's data,
or, when it was unreachable, the total, date and kind from the decoded QR payload itself. Everything
the vision fallback alone read — the lines, their amounts, and the total and date of a receipt with no
QR — becomes correctable by a reply, the same way spoken amounts are (echo plus correct, P2-1). The
receipts rule in `.claude/rules/receipts.md` is reworded to that boundary when this is built, not
before: until then the rule describes what the code does.

**What makes it more than a routing change.** A correction today deletes and replaces model-authored
line items and loses their link to `receipt_lines` and the receipt's own line order (recorded in
`deferred-from-phase-6-receipts.md`). Correcting a vision-read receipt must keep that link and order,
and keep what vision read (the `receipts`/`receipt_lines` rows) as history while the transaction's
lines carry the corrected figures. This is the same work as the phase-6 item "editing receipt lines".

**Phase 7 precedent.** Exchange-office slips (Phase 7) are the first exemption: their vision reading
is a starting point the operator corrects, because a slip has no QR at all.
