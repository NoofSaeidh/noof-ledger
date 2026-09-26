using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Startup;

internal sealed class DatabaseGate : IDatabaseGate
{
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    volatile DatabaseState state = DatabaseState.Waiting;
    volatile string? detail;

    public DatabaseState State => state;

    public string? Detail => detail;

    public void Set(DatabaseState newState, string? newDetail)
    {
        state = newState;
        detail = newDetail;

        if (newState == DatabaseState.Ready)
            ready.TrySetResult();
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        state == DatabaseState.Ready ? Task.CompletedTask : ready.Task.WaitAsync(cancellationToken);
}
