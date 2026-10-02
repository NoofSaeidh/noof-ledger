---
title: Foreign-currency spending on a fiscal receipt
status: deferred
area: persistence
since: 2026-10-02 (Phase 7 close)
related: [deferred-from-phase-6-receipts]
---
**Operator, 2026-10-01 (T-1):** not in scope — *"нет такого сценария"*. A Serbian fiscal receipt is in
dinars, and its wallet is the one the caption names, else the payment-method default of the receipt's
currency (R-3, per currency since T-13), so it normally lands on a dinar wallet. Only a caption naming a
wallet in another currency (a EUR card paying in a Serbian shop) makes it a foreign-currency spending,
and that record keeps M10's unconverted RSD line on the EUR wallet: `ForeignCharges.RewriteAsync` never
computes a charge for a `CategorizeReceipt` job (spec A-24), and the receipt echo has no charge line.

**Ready when wanted.** The charge arithmetic is shared Domain code — `ChargeTerms.ChargeFor` and
`ChargeTerms.Stated` in `src/Noof.Ledger.Domain/ChargeTerms.cs` — so the receipt path can use it without
redesign: drop the `CategorizeReceipt` exclusion, give the receipt echo the charge line the ordinary echo
already renders (`RecordEcho.ChargeLine`), and decide whether a charge stated in a receipt's caption is
honoured the way a spoken one is (T-7).
