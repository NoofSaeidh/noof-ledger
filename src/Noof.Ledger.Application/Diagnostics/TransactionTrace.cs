using Noof.Ledger.Domain;
using PaymentMethod = Noof.Ledger.Application.Receipts.PaymentMethod;
using ReceiptSource = Noof.Ledger.Application.Receipts.ReceiptSource;

namespace Noof.Ledger.Application.Diagnostics;

public sealed record ReceiptTraceLine(
    int Ordinal, string Name, decimal Quantity, string? Unit, decimal UnitPrice, decimal Total, string? CategoryNameEn);

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
    IReadOnlyList<ReceiptTraceLine> Lines);

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
