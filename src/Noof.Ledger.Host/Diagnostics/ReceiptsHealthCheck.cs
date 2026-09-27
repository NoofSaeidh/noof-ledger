using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class ReceiptsHealthCheck(
    IDatabaseGate gate, IReceiptFetchStatus status, TimeProvider timeProvider, TimeZoneInfo captureZone) : ISystemHealthCheck
{
    static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public string Name => "Receipts";

    public int Order => 80;

    public string LogCategory => "Noof.Ledger.Host.Workers.ExtractReceiptWorker";

    public Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        if (gate.State is not DatabaseState.Ready)
            return Task.FromResult(HealthOutcome.Warning("Waiting for the database"));

        if (status.LastFailureAt is { } at && timeProvider.GetUtcNow() - at < Window)
        {
            var local = TimeZoneInfo.ConvertTime(at, captureZone);
            return Task.FromResult(HealthOutcome.Warning(
                $"Tax Administration unreachable at {local:HH:mm} — receipts fall back to the photo"));
        }

        return Task.FromResult(HealthOutcome.Ok("No failed lookups in 24 h"));
    }
}
