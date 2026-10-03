namespace Noof.Ledger.Web.Components.Shared;

// A circuit shares one LedgerDbContext, and EF Core refuses a second operation on it while one runs - observed on
// /diagnostics/logs and /transactions (DiagnosticsLogs.razor's ProvideRowsAsync explains the race). Everything a page
// does against Application goes through one of these, one action after another. The semaphore is never disposed: a
// page disposed mid-action would make that action's Release throw, and a SemaphoreSlim whose wait handle is never
// read holds nothing that needs freeing.
internal sealed class OneAtATime
{
    readonly SemaphoreSlim gate = new(1, 1);

    public bool Busy { get; private set; }

    // False when the token is cancelled before or while the operation runs: the page has gone, so whatever the abandoned
    // operation then throws - a cancellation, or a clipboard, database or model call failing late - has nowhere to be
    // shown, and escaping an event handler it would end the whole circuit, the page the operator moved to included.
    public async Task<bool> RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        Busy = true;
        try
        {
            await operation(cancellationToken);
            return !cancellationToken.IsCancellationRequested;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            Busy = false;
            gate.Release();
        }
    }
}
