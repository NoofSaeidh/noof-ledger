using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Diagnostics;

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
    IReadOnlyList<RevisionView> History);

public interface ITransactionTrace
{
    Task<TransactionTrace> GetAsync(Guid transactionId, CancellationToken cancellationToken);
}
