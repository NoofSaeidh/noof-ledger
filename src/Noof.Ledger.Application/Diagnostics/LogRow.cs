namespace Noof.Ledger.Application.Diagnostics;

public sealed record LogRow(
    long Id,
    DateTimeOffset LoggedAt,
    LogSeverity Level,
    string? Source,
    string Message,
    string? Exception,
    Guid? TransactionId,
    string? PropertiesJson);

public sealed record LogFilter(
    LogSeverity MinLevel = LogSeverity.Information,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Text = null,
    string? Source = null,
    Guid? TransactionId = null);

public sealed record LogPage(IReadOnlyList<LogRow> Rows, int TotalCount);
