using Noof.Ledger.Application.Categorization;
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
}
