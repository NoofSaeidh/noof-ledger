namespace Noof.Ledger.Host.Workers;

internal sealed class BackupWorkerOptions
{
    public bool Enabled { get; init; } = true;

    string backupDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "backups");

    // appsettings.json spells this default as the literal "%LOCALAPPDATA%\NoofLedger\backups" - the
    // same convention Logging:File:Directory already uses - so a configured value has to be expanded
    // here, since Bind() never does that on its own.
    public string BackupDirectory
    {
        get => backupDirectory;
        init => backupDirectory = Environment.ExpandEnvironmentVariables(value);
    }

    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromHours(1);

    public int KeepCount { get; init; } = 14;
}
