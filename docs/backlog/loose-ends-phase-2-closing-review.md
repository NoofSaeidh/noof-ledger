---
title: Loose ends from the Phase 2 closing review
status: deferred
area: telegram
since: 2026-09-23
---
Recorded 2026-09-23 by the Phase 2 closing review (Fable 5.1, a different model family from the one
that wrote the code). Each below is a real, cheap-to-verify gap rather than a proven defect; none
blocks the phase.

**`AddCorrectionJobs` dropped the plain index on `categorization_jobs.transaction_id`.** EF's
FK-index convention does not notice the filtered unique index the migration added in its place, so
`transaction_id` now has a filtered unique index but no plain one. Consequence: a per-candidate scan
inside `ClaimAsync`'s `NOT EXISTS`, and an unindexed CASCADE FK — irrelevant at personal-ledger scale.
If it ever matters, add an explicit `HasIndex(j => j.TransactionId)` in a new migration.

**The per-transaction ordering guard has an untested branch.**
`tests/Noof.Ledger.Persistence.Tests/EfJobQueueTests.cs:363-388` covers only "an earlier `Pending` job
blocks a later one for the same transaction". Untested: an earlier job `Claimed` (in flight) also
blocking a later one — the case that actually prevents the double-application race — and "an earlier
`Failed` job does NOT block". A future rewrite that compares against `status = 0` directly would keep
this test green while losing the guarantee it names. Cheap to add both rows.

**A reply to a stale bot message is captured as a new transaction.**
`src/Noof.Ledger.Telegram/CorrectionHandler.cs:13-23` and `TelegramUpdateRouter.cs:44-46` — a reply
to a bot message that matches no record falls through to capture as a **new** spend. Realistic
triggers: answering an earlier Изменить prompt after a second tap replaced `PromptMessageId` (only the
latest prompt is remembered — see "Pressing Изменить twice" above), or replying to the poison notice.
*"нет, 1500"* then becomes a fresh transaction the model categorises. The echo/cancel net still
catches it, and the behaviour is pinned by design (a reply to anything but an echo is captured as a
new message). Suggested refinement, not built: when `repliedTo.From?.IsBot == true` and nothing
matches, answer "не нашёл запись" instead of capturing.

**`Исправляю…` can race the correction's own echo.**
`src/Noof.Ledger.Telegram/CorrectionHandler.cs:18-22` sends its "Исправляю…" edit after the
correction job is already queued. If the worker claims the job, calls the model and echoes before
that edit lands (slow Telegram, fast model), the final echo is briefly overwritten with
"Исправляю…" and no buttons until the next tap or reply. The database is correct throughout; it
self-heals on the next interaction.

**`AskAsync` can fail if the echo it replies to was deleted.**
`src/Noof.Ledger.Telegram/TelegramChatNotifier.cs:31-38` — the Изменить prompt replies to the echo
message. If the person deleted the echo, Telegram refuses ("message to be replied not found") and the
update burns three poison attempts for no reason. Consider
`ReplyParameters.AllowSendingWithoutReply = true`.
