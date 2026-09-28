---
id: P6-1
title: "Phase 6 receipts decisions (2026-09-25)"
status: decided
date: 2026-09-25
phase: Phase 6
related: [p6-2-vision-fallback-stopped-inventing-receipts]
---

Full design in `docs/specs/2026-09-25-receipts-design.md`; rules that bind future work live
in `CLAUDE.md` and `.claude/rules/receipts.md`. The operator's seven decisions (R-1..R-7), taken before implementation:

| # | Decision |
|---|---|
| R-1 | Scope: fiscal QR first; no QR → vision from the photo. A QR link sent as text is accepted too. Exchange-office slips are Phase 7. |
| R-2 | Store every receipt line. A receipt is its own record (`receipts`, `receipt_lines`); the transaction's line items reference the receipt lines. The echo lists every line with its category. |
| R-3 | Wallet: named in the photo caption → else the wallet marked default for the receipt's payment method → else the default wallet. C# decides; the model only reads the caption. |
| R-4 | The photo is not stored (Telegram keeps it; the database keeps the file id). |
| R-5 | QR read but the Tax Administration unreachable → read the lines from the photo by vision at once; the QR total checks the sum. |
| R-6 | Approach A: facts from C#, the model only categorises (and reads photos in the vision fallback). |
| R-7 | Everything logged; QR read but the tax site failed → a visible warning (echo line, Warning event, health check). |

Two further rulings were made by the closing controller once implementation surfaced questions the spec
had not settled:

**F-1 (from the final review's I-2).** Keep Edit working on a receipt's echo rather than withdraw it: a
correction of a receipt transaction re-runs `categorize_receipt` with the operator's text as an
instruction and rebuilds the line items from `receipt_lines` — the amounts never change through Edit.
Reasoning: this keeps the operator's one correction tool working uniformly across every capture kind
while still honouring R-6 (the model only categorises). Cost if wrong: the operator might have preferred
Edit withheld on a receipt entirely; reversible, since it is a routing decision, not a schema change.

**F-2 (from the re-review's N-1..N-5).** Route at claim time: any `Correct`/`Reinterpret` job on a
transaction that has a receipt goes to the receipt path, never `record_transaction` — one check,
`CategorizationWorker.TryRouteToReceiptAsync`, covers a typed reply, a voice reply, and an edited
message alike. A correction that arrives while the receipt is still being extracted is *deferred*, not
discarded — it retries on the job's own existing attempt budget until the receipt exists, rather than
inventing a second retry mechanism. A correction naming no wallet keeps the record's current wallet
(the same `KeepingTheRecordsWallet` rule text captures already followed, now keyed on "is this a
correction" rather than "does the subject have a wallet"). A request to change the date or an amount is
answered in the echo rather than silently ignored or silently applied — those values come from the
receipt, never from a correction.

**Decisions made during implementation, worth knowing before revisiting this code:**

- **ZXing.Net.Bindings.SkiaSharp, not .ImageSharp, for QR decoding.** `ZXing.Net.Bindings.ImageSharp`
  (0.16.16) pins `SixLabors.ImageSharp 1.0.4` with no `net9.0`/`net10.0` target — using it with a current
  ImageSharp risks a runtime `MissingMethodException`. Current ImageSharp also ships under the Six Labors
  Split License (free for OSI-approved open source or under a revenue threshold, commercial otherwise);
  this repo has no `LICENSE` file, so "OSI-approved" isn't something to assert on its behalf. SkiaSharp is
  MIT and `ZXing.Net`/`ZXing.Net.Bindings.SkiaSharp` are Apache-2.0 — unambiguous for a public repo, and
  the SkiaSharp binding ships an explicit `net10.0` dependency group.
- **Non-money receipt kinds (Copy, Training, Proforma, Advance) end `Cancelled`, not `Failed`.** The
  first pass called `MarkFailedAsync`, but a deliberate non-post is the same kind of decision a duplicate
  receipt already gets — not a processing error — so `Failed` would have muddied "something broke" with
  "nothing was meant to post here". Fixed in the phase's closing review (M-4): the worker now calls
  `IRecordEditor.CancelAsync` and logs a `StageFailed` row at `Categorized` so the trace page shows why,
  instead of Extracted-then-silence.
- **The Domain project owns `ReceiptSource`, `ReceiptKind` and `PaymentMethod`**, not
  `Noof.Ledger.Application.Receipts`. The shared contract (`ReceiptContracts.cs`) was written against
  Application-side mirrors first because an early task was told not to redefine it while a parallel task
  was adding the Domain versions; the closing review's M-5 consolidated on the Domain enums everywhere
  (the dependency direction is Domain ← Application, and the contract already imports Domain for
  `CurrencyCode`), deleting the Application-side twins and their cast-based mapping.
- **The defer mechanism for "a correction arrived mid-extraction" reuses the job's own retry budget**,
  rather than a second, purpose-built retry counter. `RetryAsync` on the same `Correct`/`Reinterpret` job
  bounds the wait at `MaxAttempts × 5s` (≈40s) and fails the job in the ordinary way if the receipt never
  materialises — one budget to reason about, not two. Fixed in the phase's second re-review (R2-3):
  exhausting that budget on the defer branch used to fail silently; it now computes `isLastAttempt` the
  same way `HandleModelFailureAsync` does, logs Warning 1210 and a `StageFailed` row at `Categorized`, and
  calls `NotifyFailureAsync` so the operator sees the `ComposeCorrectionFailure` echo — `docs/backlog/deferred-from-phase-6-receipts.md`
  has the full mechanism.
- **The synthetic fiscal QR is dense — version 22, 105x105 modules** (`QrDensityTests`, built from
  `SyntheticQrPayloadBuilder`'s default, non-large-block payload: 796 characters of
  `https://suf.purs.gov.rs/v/?vl=...`). At the resolution Telegram actually delivers a compressed
  photo (≤1280px long side, JPEG ~q80) the QR occupies only 15–30% of the frame on a typical receipt
  photo, which puts it at roughly 1–5 pixels per module depending on the exact crop — the regime that
  produced the production "Vision used: no QR" failure. `ZxingQrReaderRealisticPhotoTests` benchmarks
  this with synthetic photos (a receipt-shaped canvas, text-noise, the QR placed and JPEG-compressed
  the same way). **The benchmark's own downscale must use a filtered resampler.**
  `SKBitmap.Resize`'s `SKSamplingOptions.Default` is nearest-neighbour in SkiaSharp 4.151.1
  (`Filter=Nearest, Mipmap=None`, confirmed by printing it) — no phone or Telegram resampler works
  this way. A first pass of this benchmark used `SKSamplingOptions.Default` for its 2400→1280
  downscale and reported a "chaotic, non-monotonic" pass/fail sweep (17/18/19/21/29/30% decoded,
  everything else did not) as proof that nothing past that point could move a single case; that
  pattern was the benchmark's own nearest-neighbour aliasing on a pixel-perfect QR, not JPEG
  quantisation — re-running the identical sweep with `SKSamplingOptions(SKFilterMode.Linear,
  SKMipmapMode.Linear)` for the downscale alone raises the same benchmark, same reader, to 9/17 clean
  + 1/6 compounded (10/23 total, up from 8/23), a different pass set. With that filtered benchmark in
  place, giving `ZxingQrReader`'s own 2x upscale (`src/Noof.Ledger.Receipts/Qr/ZxingQrReader.cs`) a
  linear filter instead of nearest (which was pixel-duplication, so it added nothing a binarizer could
  use) is a measured, strict improvement: 17/23 (13/17 clean + 4/6 compounded), a superset of every
  case the nearest-nearest baseline passed, at 118 ms average vs 175 ms before — the fix ships.
  Corrected conclusion: nearest-neighbour resampling anywhere in this pipeline destroys exactly the
  sub-pixel information a phone's own bilinear resize preserves; a decoder given filtered pixels *can*
  recover more than one given nearest-neighbour pixels, so "nothing afterwards can help" was an
  artefact of the benchmark, not a property of JPEG quantisation. `ZxingQrReaderRealisticPhotoTests`
  now pins six cases from the filtered sweep: two that already decoded before this fix (a regression
  guard) and four that only decode with the reader's linear upscale (the improvement this change
  ships). The five further techniques tried in the same investigation — additional upscale factors,
  a `GlobalHistogramBinarizer` fallback, a contrast-stretch/grayscale pass, an unsharp-mask sharpen,
  and a finder-pattern crop — were tried only against the nearest-neighbour benchmark and were not
  re-tried after this fix; whether any of them helps further, now that the benchmark itself is
  trustworthy, is open.
- **Real fiscal QRs are denser still, and no decoder tried reads them from a photo** (2026-09-27, the
  operator's own receipts, five photos kept out of the repo). The printed codes are version ~40 (a 7x7
  grid of alignment patterns, ~177 modules a side), not the synthetic version 22. On the two
  Telegram-compressed copies (1280x720) the QR spans ~210 px, under 2 px per module. On three
  full-resolution 12 MP files sent as documents it spans 640-760 px, ~4 px per module, one of them flat
  and sharp. Not one image decoded with ZXing.Net (hybrid and global-histogram binarizers, 0.25x-2x
  scales, the QR region cropped and upscaled, blur to merge thermal-print dots), zxing-cpp 0.5.3,
  OpenCV 4.10's `QRCodeDetector`, or the WeChat CNN detector with its super-resolution model. The same
  receipts' links, sent as text, decoded and fetched exactly. So the exact path is the link, scanned by
  the phone's own camera; the photo path is the vision fallback in practice, which is why its guards
  (`P6-2`) matter. A close-up of the QR alone (~15 px per module) is untested; a heavier decoder would
  need to show a win on these real photos first, and none did.
