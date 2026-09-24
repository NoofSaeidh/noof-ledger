namespace Noof.Ledger.Persistence.Backup;

internal sealed class BackupRun
{
    public required Guid Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public required bool Succeeded { get; init; }
    public string? FileName { get; init; }
    public long? SizeBytes { get; init; }
    public string? Error { get; init; }
}
