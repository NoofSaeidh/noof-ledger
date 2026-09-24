using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class AppLogEntry
{
    public required long Id { get; init; }
    public required DateTimeOffset LoggedAt { get; init; }
    public required LogSeverity Level { get; init; }
    public string? Source { get; init; }
    public required string Message { get; init; }
    public required string Template { get; init; }
    public string? Exception { get; init; }
    public Guid? TransactionId { get; init; }
    public string? PropertiesJson { get; init; }
}
