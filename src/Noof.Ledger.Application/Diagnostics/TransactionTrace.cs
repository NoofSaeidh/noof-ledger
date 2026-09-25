namespace Noof.Ledger.Application.Diagnostics;

public sealed record TraceEvent(
    DateTimeOffset At,
    string Stage,
    int EventId,
    LogSeverity Level,
    string Message,
    string? Exception,
    string? PropertiesJson);

public sealed record RevisionView(DateTimeOffset At, string ChangeKind, string Details);

public sealed record TransactionTrace(
    Guid TransactionId,
    bool Exists,
    IReadOnlyList<TraceEvent> Events,
    IReadOnlyList<RevisionView> History);

public interface ITransactionTrace
{
    Task<TransactionTrace> GetAsync(Guid transactionId, CancellationToken cancellationToken);
}
