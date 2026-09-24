namespace Noof.Ledger.Application.Backup;

public sealed record BackupRunRecord(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Succeeded, string? FileName, long? SizeBytes, string? Error);

public sealed record BackupStatus(DateTimeOffset? LastSuccessAt, bool LastRunFailed, string? LastError);

public sealed record DumpResult(bool Succeeded, string? Error);
