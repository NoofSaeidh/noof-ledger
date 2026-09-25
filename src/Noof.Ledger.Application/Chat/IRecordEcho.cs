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

    EchoMessage Compose(CategorizationSubject record);
    EchoMessage ComposeCorrectionFailure(CategorizationSubject record);
    EchoMessage ComposeHeardNothing(CategorizationSubject record);
    string ComposeCategorisingReceipt(int lineCount);
    EchoMessage ComposeReceiptDuplicate(DateOnly? occurredOn, decimal total, CurrencyCode currency);

    // Read back from the database exactly as Compose is (D4): record.Lines already carries the
    // categories the worker just wrote. receipt supplies what a plain capture has no equivalent of -
    // the shop, its lines' own order, and the source warnings (R-2, R-7).
    EchoMessage ComposeReceipt(CategorizationSubject record, ReceiptView receipt);

    // The receipt equivalent of Failure (R-6): a copy/training/proforma/advance slip is not a
    // purchase, so nothing was posted - the transaction is marked Failed exactly like Failure's own
    // case, and Edit still lets the person record it by hand as an ordinary text correction.
    EchoMessage ComposeReceiptNotRecorded(Receipts.ReceiptKind kind);
}
