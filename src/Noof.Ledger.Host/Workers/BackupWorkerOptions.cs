namespace Noof.Ledger.Host.Workers;

internal sealed class BackupWorkerOptions
{
    public bool Enabled { get; init; } = true;

    public string BackupDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "backups");

    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromHours(1);

    public int KeepCount { get; init; } = 14;
}
