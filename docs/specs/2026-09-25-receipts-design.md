# Phase 6 — Receipts (design)

**Status:** approved in conversation 2026-09-25 (approach A); implementation authorised by the operator
("A … и начинай реализацию").

## Goal

A photographed Serbian fiscal receipt sent to the bot becomes one transaction whose line items are the
receipt's own lines, in the receipt's order, each with a category; the shop is one merchant row, and the
same shop next time is a key hit costing zero merchant tokens. Receipts without a readable fiscal QR, or
when the Tax Administration's site is down, are read from the photo by a vision model. Every step is
logged on the transaction's trace; a degraded path is visible, never silent.

## Decisions (operator's, 2026-09-25)

| # | Decision |
|---|---|
| R-1 | Scope: fiscal QR first; no QR → vision from the photo. A QR link sent as text is accepted too. Exchange-office slips belong to Phase 7. |
| R-2 | Store every receipt line. A receipt is its own record (`receipts`, `receipt_lines`); the transaction's line items reference the receipt lines. The echo lists every line with its category. |
| R-3 | Wallet: named in the photo caption → else the wallet marked default for the receipt's payment method → else the default wallet. C# decides; the model only reads the caption. |
| R-4 | The photo is not stored (Telegram keeps it; the database keeps the file id). |
| R-5 | QR read but the Tax Administration unreachable → read the lines from the photo by vision at once; the QR total checks the sum. |
| R-6 | Approach A: facts from C#, the model only categorises (and reads photos in the vision fallback). |
| R-7 | Everything logged; QR read but the tax site failed → a visible warning (echo line, Warning event, health check). |

*Amended by Phase 7 — `docs/specs/2026-10-01-transfers-and-exchange-design.md` (2026-10-01): R-1's exchange-office slips
are built there (T-8), and R-3's payment-method default is per currency (T-13).*

## 1. Data

- `receipts` (one per transaction that came from a receipt): `id uuid PK`, `transaction_id uuid FK unique`,
  `source smallint` (`FiscalQr` = the Tax Administration's data, `Vision` = read from the photo),
  `verification_url text null`, `seller_tax_id text null` (PIB), `seller_name text null`,
  `seller_address text null`, `location_name text null`, `fiscal_number text null` (ПФР број рачуна),
  `issued_at timestamptz null` (ПФР време), `total numeric(19,4) not null`, `currency char(3) not null`
  (RSD for fiscal receipts), `receipt_kind smallint` (Sale, Refund, Copy, Training, Proforma, Advance),
  `payment_method smallint null` (Card, Cash, Transfer, Voucher, Other, Mixed), `qr_total numeric(19,4) null`,
  `telegram_file_id text null`, `created_at timestamptz`. Unique index on `(seller_tax_id, fiscal_number)`
  where both are not null — the duplicate detector.
- `receipt_lines`: `id uuid PK`, `receipt_id FK`, `ordinal int` (1-based, receipt order), `name text`,
  `quantity numeric(19,4)`, `unit text null`, `unit_price numeric(19,4)`, `total numeric(19,4)`,
  `tax_label text null`. Unique `(receipt_id, ordinal)`.
- `line_items` gains `ordinal int` (order within the transaction; existing rows backfilled in id order per
  transaction) and `receipt_line_id uuid null FK`.
- `merchants` gains `tax_id text null` with a unique index — a PIB hit resolves the merchant with no model
  tokens.
- `wallets` gains `default_for_payment smallint null` (Card or Cash) with a unique index per value.
- `receipts` and `receipt_lines` are evidence: never edited after insert (corrections change line items and
  write a revision, as today). They are not in `wallet_balances`; money still flows only through
  `entries`.
- Domain: `CaptureKind.Photo`, `JobKind.ExtractReceipt`, `JobKind.CategorizeReceipt`; `Receipt`,
  `ReceiptLine`, `ReceiptSource`, `ReceiptKind`, `PaymentMethod`.

## 2. Pipeline

1. **Intake (Telegram).** A photo, an image document, or a text that contains a
   `https://suf.purs.gov.rs/v/?vl=` link → `ICaptureStore.CaptureReceiptAsync` creates the transaction
   (`CaptureKind.Photo`, status pending), stores caption/text and the Telegram file id, enqueues
   `ExtractReceipt`, and the bot acknowledges ("Reading the receipt…"). Stage **Received**.
2. **ExtractReceiptWorker** (Host): claims `ExtractReceipt`.
   - Photo: download (`IReceiptPhotoSource`), decode the QR (`IQrReader`). Text link: take the URL.
   - QR found: decode `vl` offline (`VerificationUrl.Decode`) → total, time, seller id, receipt kind. Fetch
     the receipt (`IFiscalReceiptClient`) → parse (`FiscalJournalParser`) → `receipts` + `receipt_lines`
     with source `FiscalQr`.
   - QR found but the fetch fails (network, 5xx, timeout, unparseable) → Warning event
     `ReceiptFetchFailed`, record the failure time for the health check, then vision (source `Vision`,
     `qr_total` = the offline total).
   - No QR (photo) → vision. Text link that fails to fetch → no photo to fall back on: the transaction
     fails with a clear echo.
   - Duplicate (`seller_tax_id` + `fiscal_number` already recorded) → the new transaction is cancelled and
     the echo links the existing one.
   - Stage **Extracted** with `Source`, `Lines`, `Total`, `QrTotal`, `Mismatch` properties; then enqueue
     `CategorizeReceipt`.
3. **CategorizeReceiptWorker** (Host, or the categorisation worker with the new kind): one model call with
   the forced strict tool `categorize_receipt`: input = the lines (name, quantity, total), seller name/PIB,
   caption, wallets, categories; output = one `category_slug` per line ordinal, the merchant's canonical
   name (only when the PIB is not already known), and the wallet named in the caption if any. C# builds the
   line items (amounts from `receipt_lines`, never from the model), picks the wallet (R-3), resolves the
   merchant by PIB → alias → canonical name, writes entries and a revision. Receipt kind `Refund` →
   transaction kind `Income`. Stage **Categorized**, **Persisted**.
4. **Echo**: shop, date, wallet, every line `name — amount — category` in order, total; warnings: fetch
   failed (vision used), sum of lines ≠ QR total, duplicate. Stage **Replied**. Cancel/Edit work as today.

`TransactionStages` gains `Extracted` (EventId 5006) between Transcribed and Categorized in `Ordered`;
`ReceiptFetchFailed` is EventId 5010 (Warning). The trace page shows Extracted; Transcribed shows "—" for
receipts.

## 3. Fiscal QR and the Tax Administration

- `VerificationUrl.Decode(string url)` (Receipts): base64 `vl` → version, requestedBy, signedBy,
  totalCounter, transactionTypeCounter, total (LE int64 / 10000, RSD), issuedAt (BE int64 ms, Unix),
  invoice type, transaction type, buyer id; MD5 check of the payload; malformed → a typed failure, never an
  exception escaping the worker. Layout ported from turanjanin/serbian-fiscal-receipts-parser (MIT) —
  credited in the source.
- `IFiscalReceiptClient.FetchAsync(url)`: GET the verification URL with `Accept: application/json`; a
  descriptive User-Agent; timeout 15 s; no retries beyond one; the `HttpClient` has `RemoveAllLoggers()`.
  One request per receipt — never bulk.
- `FiscalJournalParser.Parse(journal)`: header (PIB, name, location, address), item lines (name with
  unit, then `unitPrice quantity total`), totals, payment lines (Платна картица / Готовина / Пренос на рачун
  / Ваучер …, Cyrillic and Latin), ПФР време, ПФР број рачуна, counter. Both scripts.
- `IQrReader.Read(Stream image)`: ZXing.Net + `ZXing.Net.Bindings.ImageSharp` (no System.Drawing),
  `TryHarder`, `TryInverted`, `QR_CODE` only; on failure retry on a downscaled and an upscaled copy.
- Test fixtures are **synthetic** (public repo): generated `vl` payloads, hand-written journals, generated
  QR images. No real receipt ever enters the repo.

## 4. Vision

- `IReceiptVision.ReadAsync(image, qrTotal?)` (Application seam, Ai implementation): the model gets the
  image as `DataContent` and a forced strict tool `read_receipt`: seller name, PIB if printed, date/time,
  lines (name, quantity, unit price, total), total, payment method. Amounts are JSON numbers read straight
  into `decimal`. Provider code stays under `src/Noof.Ledger.Ai/Anthropic/`; the tool loop pattern and the
  guard client as for `record_transaction`.
- Live calls only in the opt-in live suite.

## 5. Logging, health, UI

- Every stage logs through `[LoggerMessage]` with `TransactionLogScope`; events: Received (CaptureKind
  Photo), `QrDecoded` (Debug), `ReceiptFetched` (Information: lines, total), `ReceiptFetchFailed`
  (Warning: reason, status code — never the full URL; the `vl` value is a personal fiscal record and is
  logged only as its first 8 characters), `VisionUsed` (Information: reason), Extracted, Categorized,
  Persisted, Replied, StageFailed.
- Health check **Receipts** (Host): Warning when a fetch failed in the last 24 h ("Tax Administration
  unreachable at HH:mm — receipts fall back to the photo"); Ok otherwise. Added to `HealthCheckNames`.
- `/wallets`: a "Default for card" / "Default for cash" choice per wallet.
- Transaction trace and `/transactions`: a receipt section (shop, PIB, fiscal number, source, lines with
  their categories); the transactions grid shows a receipt marker.

## 6. Testing

TDD. Fast: `VerificationUrl` (synthetic payloads incl. malformed, MD5 mismatch, refund, buyer id),
journal parser (Cyrillic, Latin, card/cash/mixed, refund), QR reader (generated images incl. rotated and
small), wallet choice, merchant resolution by PIB, echo formatting, router intake of photo/document/link.
Ai: `read_receipt` and `categorize_receipt` request bodies (strict, forced tool, image content) captured
over HTTP. DB: migration, receipts/lines/line items round trip, duplicate index, backfilled ordinals.
Host: workers with fakes, incl. fetch failure → vision → Warning + health. E2E: trace page shows the
receipt section; /wallets payment defaults. Live (opt-in): a synthetic receipt image through vision.

## Out of scope

Exchange-office slips (Phase 7); storing photos; bulk import; editing receipt lines; a product →
category dictionary (possible later cache, like merchant aliases).
