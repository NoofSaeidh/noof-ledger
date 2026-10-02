---
title: Deferred from Phase 6 (receipts)
status: deferred
area: other
since: 2026-09-26
---
Recorded 2026-09-26 closing Phase 6. Named explicitly out of scope by the spec, or found and parked
during implementation and its closing review and two re-reviews (Fable 5.1).

**A product → category dictionary as a cache in front of the model (R-Q7-adjacent).** Named out of
scope by the design as "possible later cache, like merchant aliases" — the same shape as
`IMerchantDirectory`'s write-once alias table, but keyed on a receipt line's product name rather than a
merchant. Would cut categorisation tokens on repeat purchases at the same shop; not built because there
is no real usage data yet to say which products repeat often enough to be worth caching.

**Editing receipt lines.** Named out of scope by the design (R-4's sibling: "storing photos" is
declined, editing lines is simply not built). A correction today can change a line's category or the
transaction's wallet, never a receipt line's name, quantity or price — those come from the fiscal
record or the vision read, and F-2 (`docs/decisions/p6-1-receipts-decisions.md`) answers a request to change one in
the echo rather than silently applying or ignoring it.

**Storing photos — declined, R-4.** The photo is never kept, in the database or anywhere in the repo;
only Telegram's `file_id` is stored, which only resolves while Telegram itself keeps the file. A
re-transcription or re-extraction corpus (the voice equivalent is recorded under Phase 3 above) would
need the bytes in the database, and therefore in every backup — the same privacy decision as keeping
voice audio, not a default to reach for.

**Bulk import.** Named out of scope by the design. Nothing about the receipt pipeline assumes one
receipt per message; a bulk path would be a new intake surface (a folder of photos, an export from
somewhere), not a change to extraction or categorisation.

**The Telegram 4096-character echo limit is handled by a fixed 40-line threshold, not a measured
length.** `RecordEcho.ComposeReceipt` lists the first 40 lines individually then groups the rest by
category once a receipt has more than 40 lines, rather than composing the full text, measuring it, and
falling back only if it would actually overflow. Deliberate (Task 5's report): the concrete rule the
design gives is the 40-line cutoff, no real receipt is long enough for the two paths to differ in
practice, and a two-pass measure-then-maybe-rebuild is the kind of ceremony `CLAUDE.md` §3 asks to skip
for a condition that cannot occur. Revisit if a receipt ever actually needs the distinction — a shop
with unusually long product names could in principle overflow within 40 lines.

**Fixed (Phase 6 second re-review, R2-3) — a correction deferred during a long extraction outage no
longer exhausts its retry budget silently.** (`docs/decisions/p6-1-receipts-decisions.md`'s defer-mechanism note.) A
correction that arrives while a receipt is still being extracted is deferred on the job's own retry
budget (`MaxAttempts × 5s`, ≈40s) rather than discarded. `TryRouteToReceiptAsync`'s defer branch now
computes `isLastAttempt` the same way `HandleModelFailureAsync` does; on the final deferral it logs
Warning 1210 (`ReceiptCorrectionDeferralExhausted`), logs `StageFailed` for the `Categorized` stage the
same way every other terminal retry path does (so the trace page shows where the job died, not
nothing), and calls `NotifyFailureAsync` — which renders the `ComposeCorrectionFailure` echo in Telegram
and leaves the record itself untouched (`MarkFailedAsync` only runs for a first reading, never a
correction). The window this needs (extraction failing to finish within ~40 seconds of a correction
arriving) is narrow — reachable only via the same race N-4 traced.

**A URL-only reply to a non-receipt transaction silently becomes a plain re-read, with no
"send the link separately" notice.** Found during the P6-URL branch's Fable 5.1 review, not built
(minor, backlog). `CorrectionHandler.HandleEditAsync` checks the target already has a fiscal receipt
(`IReceiptStore.GetVerificationUrlAsync`) before deciding a pasted link is a *different* receipt and
sending `NewReceiptLinkMustBeSentSeparately` — but `TryHandleReplyAsync` (a Telegram *reply*, as
opposed to an edit) has no such check. A reply whose text is nothing but a verification URL, to a
transaction with no receipt row at all (an ordinary text/voice capture, or a failed link capture),
still becomes a `Correct` job; `CategorizationWorker.CorrectionFor` strips the URL via
`FiscalVerificationUrl.StripUrl`, gets back `null` (the "empty means none" contract), and the job
re-runs the categoriser with `Correction = null` — an unannounced re-read, not the "here's a receipt"
the person meant. Not built because it needs a design decision this branch's scope did not cover:
whether a URL-only reply to a receipt-less transaction should get its own notice text (distinct from
`NewReceiptLinkMustBeSentSeparately`, which talks about a *second* receipt), silently start extracting
the link as this transaction's own receipt, or something else — `TryHandleReplyAsync` does not know at
that point whether the target has a receipt, so wiring in a check is straightforward once the wording
is decided.

**The defer branch relies on `EfJobQueue`'s claim ordering, not an explicit dependency.** The same
defer mechanism only works because `ClaimAsync` already refuses to claim a job while an earlier job for
the same transaction is `Pending`/`Claimed` — a correction that arrives mid-extraction is therefore
almost never actually claimed in the deferred state at all; it waits behind the still-running
`ExtractReceipt` job instead. The deferral code exists for the narrow window between that job failing
and its transaction status actually flipping away from `Captured`. This is documented behaviour, not
untested — `CategorizationWorkerTests` covers the deferred branch directly — but the *reason* it is
rarely exercised is an ordering guarantee owned by a different class, worth knowing before either one
changes independently.

**Items carried from the phase's second closing re-review (R2-n), all now fixed:**

- **R2-2 (minor), fixed.** The N-2 refusal (an edited link capture that would re-file as a different
  receipt) used to overwrite the transaction's own echo with a bare refusal notice, dropping the
  Cancel/Edit buttons and the visible summary until a later reply corrected the record.
  `CorrectionHandler.HandleEditAsync` now posts the refusal through `chatNotifier.SendAsync` as its own
  message; the echo is never touched.
- **R2-4 (minor), fixed.** After a Cancel and Restore, a non-money receipt (Copy/Training/Proforma/Advance)
  landing `Captured` with no job pending used to render the generic "Recording…" acknowledgement forever.
  `RecordEcho.ComposeReceipt`'s `Captured` branch now checks `receipt.Kind.IsNonMoneyKind()`: a non-money
  slip renders `ComposeReceiptNotRecorded(receipt.Kind)` (with `[Edit]`); an ordinary receipt still being
  extracted renders `ReadingReceipt` instead.
- **R2-5 (minor, pre-existing), fixed.** An edited photo *caption* used to be dropped silently — Telegram
  delivers a caption edit as `Caption`, not `Text`, and `CorrectionHandler.HandleEditAsync` returned before
  finding the transaction at all. It now falls back to `Caption` when `Text` is absent and the edited
  message is a photo or document; a voice note's own caption is still ignored (its correction text is its
  transcript, not a caption).
- **R2-6 (documentation, low stakes), fixed.** The comment in `tests/Noof.Ledger.TestKit/DatabaseSettings.cs`
  now says plainly that the missing `CommandTimeout` is "the most likely explanation" for the flaky Npgsql
  read timeouts, not a traced root cause — matching the closed E2E-teardown-flake entry above, which
  carries the same caveat.

**Cancel/Restore on a receipt loses the receipt-specific echo styling.** `RecordActionHandler` renders a
Cancel/Restore through the generic `IRecordEcho.Compose`, not `ComposeReceipt` — the shop, location and
warning lines are gone after a Cancel/Restore round trip, though the record, its lines, wallet and
balance are all still correct underneath. Documented rather than fixed (Task 5's own call): making
Cancel/Restore receipt-aware needs an `IReceiptStore` lookup on every Cancel/Restore for every
transaction kind, for a purely cosmetic loss.

**A corrected receipt transaction's line items lose their link back to `receipt_lines`.**
`EfCategorizationStore.ApplyAsync`'s existing delete-and-replace for a `Correct`/`Reinterpret` job
removes every model-authored line regardless of whether it carries a `ReceiptLineId`, and the
replacement lines a text correction builds never set one. The `receipts`/`receipt_lines` rows themselves
are untouched — only the `line_items` ↔ `receipt_lines` link and the original receipt ordering are lost
on a corrected line. Documented, not fixed, by the same task that built the link.

**A separate, stronger model for `read_receipt`, and choosing a model in the UI — deferred by the
operator, 2026-09-27.** `read_receipt` stays on Haiku for now, same as every other tool call. Evidence
this was considered rather than overlooked: production reads on 2026-09-27 (before the "do not invent"
prompt and schema fix that day, `.claude/rules/receipts.md`) showed Haiku fabricating whole receipts from
low-quality photos — a real 4-line 1570.96 RSD cash receipt read back as "BISIBONSKA ŠTAMPA 110 RSD, 1
line", then as "МИНИСТЕРЕЛНИ 110 RSD" with an invented PIB, another as "MAXI HOLDING 2322 RSD, 16 lines"
whose lines summed to 7664, and one that took the capture's own location line for the PIB and the
capture time for the issue time. The `readable`/`unreadable_reason` fix and the "Record anyway"
confirmation flow (both 2026-09-27) make Haiku's mistakes visible and non-destructive rather than
requiring a stronger model outright; a model that reads more receipts correctly on the first try, or
letting the operator pick a model per call from Settings the way `IChatClientFactory` already permits at
the wiring level, is future work if the confirmation rate turns out to be high in real use.

**`SkiaReceiptImageScaler` corrects only the three rotation-only EXIF origins** (`BottomRight` = 180deg,
`RightTop`/`LeftBottom` = 90deg) that a phone camera or a scanner's own upright pass produce. The four
mirrored origins (`TopRight`, `BottomLeft`, `LeftTop`, `RightBottom`) — a horizontally- or
vertically-flipped scan, not a phone photo — are left untransformed. Not observed in production; add
`SKCanvas.Scale` flips for these if a flipped receipt photo ever surfaces.

**A fiscal link in a photo's caption, and several links in one message** (operator, 2026-09-27;
deferred). Today a photo whose caption carries a fiscal link is captured by its photo alone: the link
is ignored, and `CapturedReceipt`/`ck_transactions_capture_has_content` allow exactly one source
(`AddReceiptCaptureSourceXor`). Real fiscal QRs did not decode from any of the operator's photos
(`docs/decisions/p6-1-receipts-decisions.md`, Phase 6 QR entry), so that photo nearly always goes through the vision
fallback while an exact link was right there.
- **Operator's preference: the link wins when present.** Capture it as a link, since that gives exact
  Tax Administration data. Still to decide when building it: keep the photo's file id as a
  vision-fallback source for when the site is down (that relaxes the XOR constraint and needs a new
  migration), or drop the photo (then a down site gives the link capture's usual "send a photo instead"
  reply).
- **Several links in one message or caption** are probably several receipts and should become separate
  records, one per link, rather than only the first. Today `IFiscalVerificationUrl.TryFind` takes the
  first link and `StripUrl` removes all of them from the prompt text.

**`TelegramVoiceFileSource` has no size cap (found during the p6-dlcap fix round, 2026-09-27).**
`TelegramReceiptPhotoSource` refuses a photo over 10 MB, both from `GetFile`'s reported size and from
the bytes actually received during download (`SizeLimitedBuffer`), because a Telegram-reported size can
be missing, stale or simply wrong. The voice path (`GetInfoAndDownloadFile` into an unbounded
`MemoryStream`) has never had an equivalent check — confirmed absent from the start (`git log -S
MaxBytes` on it is empty), not a regression. Worth the same guard once a voice note has actually been
seen large enough to matter; no such case has shown up yet.

**Copilot findings deferred at the PR #3 close** (operator, 2026-09-27: after round 7, only security
bugs are fixed in this PR; the rest is recorded here).
- **The trace page's summary and receipt section order a receipt's lines differently.** The summary
  query in `EfTransactionTrace` (~:117) orders line items by `li.Id`, a random GUID, while the receipt
  section orders by `ReceiptLine.Ordinal`. The summary should order by `li.Ordinal` too, with a
  multi-line regression case, so both follow the receipt-order contract.
- **`TestHostLoggingDeleteTests` assumes Windows file-sharing semantics.** On a Unix runner an open
  file can still be unlinked, so the "directory remains while held" assertion fails. Harmless today
  because the app and its tests run only on Windows. Make that assertion conditional on
  `OperatingSystem.IsWindows()`, keeping the final-cleanup assertion on every OS, if the suite ever
  runs on Linux or CI.
