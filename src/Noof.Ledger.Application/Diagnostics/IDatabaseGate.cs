namespace Noof.Ledger.Application.Diagnostics;

public enum DatabaseState { Waiting, Migrating, Ready, Failed }

public interface IDatabaseGate
{
    DatabaseState State { get; }

    string? Detail { get; }

    Task WaitUntilReadyAsync(CancellationToken cancellationToken);
}
