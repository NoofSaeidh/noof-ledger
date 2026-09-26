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
    EchoMessage NotAFiscalReceiptLink { get; }
    EchoMessage ReceiptFetchUnreachableLinkOnly { get; }
    EchoMessage ReceiptReadFailure { get; }
    EchoMessage ReceiptVisionNotConfigured { get; }

    EchoMessage Compose(CategorizationSubject record);
    EchoMessage ComposeCorrectionFailure(CategorizationSubject record);
    EchoMessage ComposeHeardNothing(CategorizationSubject record);
    string ComposeCategorisingReceipt(int lineCount);
    // originalCancelled (M-11, Phase 6 final review): the duplicate index ignores status, so a
    // receipt whose earlier transaction was Cancelled is rejected forever unless this says Restore
    // is the way back - the operator has no other way to discover it.
    EchoMessage ComposeReceiptDuplicate(DateOnly? occurredOn, decimal total, CurrencyCode currency, bool originalCancelled = false);

    // Read back from the database exactly as Compose is (D4): record.Lines already carries the
    // categories the worker just wrote. receipt supplies what a plain capture has no equivalent of -
    // the shop, its lines' own order, and the source warnings (R-2, R-7). amountChangeDeclined (I-2,
    // Phase 6 final review) is true only after a correction asked for a different amount than the
    // receipt shows - amounts never change, so the echo says so instead of silently ignoring it.
    EchoMessage ComposeReceipt(CategorizationSubject record, ReceiptView receipt, bool amountChangeDeclined = false);

    // The receipt equivalent of Failure (R-6): a copy/training/proforma/advance slip is not a
    // purchase, so nothing was posted - the transaction is Cancelled, a deliberate non-post rather
    // than a processing failure (M-4, Phase 6 final review), and Edit still lets the person record it
    // by hand as an ordinary text correction.
    EchoMessage ComposeReceiptNotRecorded(ReceiptKind kind);
}
