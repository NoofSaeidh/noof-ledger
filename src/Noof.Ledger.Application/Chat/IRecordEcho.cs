using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Chat;

public interface IRecordEcho
{
    string Acknowledgement { get; }
    string Correcting { get; }
    string EditPrompt { get; }
    EchoMessage Failure { get; }
    string Transcribing { get; }
    EchoMessage HeardNothing { get; }
    EchoMessage TranscriptionFailure { get; }

    // Phase 6 receipts: the photo/link acknowledgement, the document rejection, and the terminal
    // outcomes ExtractReceiptWorker itself reports (Task 5 owns the final categorised echo).
    string ReadingReceipt { get; }
    string OnlyPhotosSupported { get; }

    // A message of a kind the bot cannot read at all (a sticker, video, video note, audio,
    // animation, location, contact, ...) - distinct from OnlyPhotosSupported, which answers a
    // document the bot DID recognise but whose MIME type it cannot decode.
    string UnsupportedMessageType { get; }
    EchoMessage NotAFiscalReceiptLink { get; }
    EchoMessage ReceiptFetchUnreachableLinkOnly { get; }
    EchoMessage ReceiptReadFailure { get; }
    EchoMessage ReceiptVisionNotConfigured { get; }

    // N-2 (Phase 6 re-review): editing a link-capture message into a DIFFERENT fiscal receipt link is
    // a new receipt, not a correction of this one - CorrectionHandler answers with this instead of
    // reaching Reinterpret, and nothing about the existing record changes.
    EchoMessage NewReceiptLinkMustBeSentSeparately { get; }

    EchoMessage Compose(CategorizationSubject record);
    EchoMessage ComposeCorrectionFailure(CategorizationSubject record, RecordFailureReason reason = RecordFailureReason.None);
    EchoMessage ComposeHeardNothing(CategorizationSubject record);

    // A failed attempt that will still be retried (ops/RUNBOOK.md's "When an attempt fails"
    // paragraph: never a raw exception message - the failure is classified into one of a fixed set
    // of safe categories internally, per pipeline, so no caller outside this assembly ever needs to
    // name that classification). nextAttemptLocal is already converted to the capture time zone by
    // the caller; these only format it.
    EchoMessage ComposeCategorizationRetryNotice(string step, Exception failure, DateTimeOffset nextAttemptLocal);
    EchoMessage ComposeReceiptCategorizationRetryNotice(Exception failure, DateTimeOffset nextAttemptLocal);
    EchoMessage ComposeReceiptExtractionRetryNotice(Exception failure, DateTimeOffset nextAttemptLocal);
    EchoMessage ComposeTranscriptionRetryNotice(string step, Exception failure, DateTimeOffset nextAttemptLocal);
    string ComposeCategorisingReceipt(int lineCount);
    // originalCancelled (M-11, Phase 6 final review): the duplicate index ignores status, so a
    // receipt whose earlier transaction was Cancelled is rejected forever unless this says Restore
    // is the way back - the operator has no other way to discover it.
    EchoMessage ComposeReceiptDuplicate(DateOnly? occurredOn, decimal total, CurrencyCode currency, bool originalCancelled = false);

    // Read back from the database exactly as Compose is (D4): record.Lines already carries the
    // categories the worker just wrote. receipt supplies what a plain capture has no equivalent of -
    // the shop, its lines' own order, and the source warnings (R-2, R-7). unsupportedChange (I-2, Phase
    // 6 final review; generalised from a plain bool to date|amount|none by N-8, Phase 6 re-review) is
    // set only after a correction asked for a different date or amount than the receipt shows - neither
    // ever changes, so the echo says so instead of silently ignoring it.
    EchoMessage ComposeReceipt(
        CategorizationSubject record, ReceiptView receipt, UnsupportedChangeKind unsupportedChange = UnsupportedChangeKind.None);

    // The receipt equivalent of Failure (R-6): a copy/training/proforma/advance slip is not a
    // purchase, so nothing was posted - the transaction is Cancelled, a deliberate non-post rather
    // than a processing failure (M-4, Phase 6 final review), and Edit still lets the person record it
    // by hand as an ordinary text correction.
    EchoMessage ComposeReceiptNotRecorded(ReceiptKind kind);

    // 2026-09-27: the vision fallback's own honest "I could not read this" (read_receipt's readable:
    // false, or a total/every line it left null) - distinct from ReceiptReadFailure, which is every
    // other terminal vision failure (no tool call, an empty payload).
    EchoMessage ReceiptUnreadable { get; }

    // 2026-09-27: a vision-read receipt ExtractReceiptWorker judged not to add up (its lines don't sum
    // to its total, or its printed tax id is not exactly 9 digits) - never shown for a fiscal QR/SUF
    // receipt. Shows exactly what was read and the specific problem(s), computed here (mismatch
    // arithmetic + wording, the same job ReceiptWarnings already does for the recorded echo);
    // RecordAction.RecordAnyway queues CategorizeReceipt, Cancel withdraws the capture.
    // kindUnclear: ReceiptContracts.cs, next to ReceiptVisionResult, for why Receipt.Kind can be a
    // default guess (Sale) rather than a read fact.
    EchoMessage ComposeReceiptNeedsConfirmation(ExtractedReceipt receipt, bool taxIdMalformed = false, bool kindUnclear = false);

    // The same prompt rebuilt from what was actually stored - RecordActionHandler's Cancel/Restore and
    // ExtractReceiptWorker's own C-1 replay of a still-unconfirmed job both need to turn a stored
    // ReceiptView back into this prompt without ever having an ExtractedReceipt to hand. A malformed
    // printed tax id, or an unclear kind, is never recoverable here (the stored Receipt.Kind is already
    // a concrete value, and ChatReceiptVision only ever stores a well-formed tax id), so a replay or a
    // Restore whose only original reason was either one shows this prompt with no listed problem -
    // docs/decisions/p6-2-vision-fallback-stopped-inventing-receipts.md.
    EchoMessage ComposeReceiptNeedsConfirmation(ReceiptView receipt, bool taxIdMalformed = false, bool kindUnclear = false);

    // 2026-09-27: Cancel on a receipt still awaiting confirmation (IReceiptStore.IsAwaitingConfirmationAsync)
    // has no categorised line items to show - record.Lines is empty, since CategorizeReceipt never ran.
    // Falling back to ComposeReceipt's generic Cancelled rendering produced a "Total: " with nothing
    // after it and a false "Lines add up to 0.00" warning; this shows the receipt's own stored total
    // instead and offers only Restore, which RecordActionHandler routes back to
    // ComposeReceiptNeedsConfirmation rather than the dead-end "Reading the receipt…" with no buttons.
    EchoMessage ComposeReceiptCancelledUnconfirmed(CategorizationSubject record, ReceiptView receipt);

    // Phase 7 exchange-office slips (spec §3-§4). A clean slip saved for RecordExchange.
    string RecordingExchange { get; }

    // A held slip (ExtractedExchange.Assess): what was read and why it was held, with RecordAnyway and Cancel.
    // Built from the stored slip, so the first echo, a replayed job and a Restore print the same prompt.
    EchoMessage ComposeSlipNeedsConfirmation(ExchangeSlipView slip);

    // Cancel on a held slip: nothing was recorded, so it shows the slip itself and offers only Restore.
    EchoMessage ComposeSlipCancelledUnconfirmed(ExchangeSlipView slip);

    // A slip number another transaction already recorded under the same PIB (IReceiptStore.SaveExchangeSlipAsync).
    EchoMessage ComposeSlipDuplicate(DateOnly? occurredOn, ExtractedExchange evidence, bool originalCancelled = false);
}
