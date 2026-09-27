using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Diagnostics;

public sealed record ReceiptTraceLine(
    int Ordinal, string Name, decimal Quantity, string? Unit, decimal UnitPrice, decimal Total, string? CategoryNameEn);

// AwaitingConfirmation (2026-09-27) is derived, never stored: a vision receipt with no CategorizeReceipt
// job yet - the operator's own "Record anyway" has not been pressed - the same, status-independent
// IReceiptStore.IsAwaitingConfirmationAsync RecordActionHandler's Cancel/Restore also reads, so a
// Cancelled-but-still-unconfirmed receipt shows the same way here as it does in the bot. Problems is
// recomputed from the stored lines and total (a sum-vs-total mismatch); a malformed printed tax id is
// never recoverable here once ChatReceiptVision has already dropped it to null, so that reason - when
// it was the only one - shows only as AwaitingConfirmation with an empty Problems list
// (docs/OPEN-QUESTIONS.md P6-2).
public sealed record ReceiptTraceView(
    ReceiptSource Source,
    string? SellerName,
    string? LocationName,
    string? SellerAddress,
    string? SellerTaxId,
    string? FiscalNumber,
    DateTimeOffset? IssuedAt,
    PaymentMethod? PaymentMethod,
    decimal Total,
    CurrencyCode Currency,
    decimal? QrTotal,
    IReadOnlyList<ReceiptTraceLine> Lines,
    bool AwaitingConfirmation = false,
    IReadOnlyList<string>? Problems = null);

public sealed record TraceEvent(
    DateTimeOffset At,
    string Stage,
    int EventId,
    LogSeverity Level,
    string Message,
    string? Exception,
    string? PropertiesJson,
    string? FailedStage,
    string? Reason);

public sealed record RevisionView(DateTimeOffset At, string ChangeKind, string Details);

public sealed record TraceLineItem(string Description, Money Amount, string? CategoryName);

public sealed record TransactionSummary(
    string? RawText,
    CaptureKind CaptureKind,
    DateTimeOffset ReceivedAt,
    TransactionStatus Status,
    TransactionKind Kind,
    string? WalletName,
    DateOnly OccurredOn,
    IReadOnlyList<TraceLineItem> LineItems);

public sealed record TransactionTrace(
    Guid TransactionId,
    bool Exists,
    TransactionSummary? Summary,
    IReadOnlyList<TraceEvent> Events,
    IReadOnlyList<RevisionView> History,
    ReceiptTraceView? Receipt = null);

public interface ITransactionTrace
{
    Task<TransactionTrace> GetAsync(Guid transactionId, CancellationToken cancellationToken);
}
