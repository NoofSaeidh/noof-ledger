using System.Globalization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Fx;

namespace Noof.Ledger.Persistence.Fx;

// Rates are published once a day and fetched about as often; a few days without one is the PC having been off, which
// the summaries survive by marking what they convert with an older rate "≈" (8b spec §1). The day is UTC's, as
// as_of_date is. While the database is not ready it answers without reading it, as Migrations does.
internal sealed class FxRatesHealthCheck(IFxRateStore rates, IDatabaseGate gate, TimeProvider timeProvider) : ISystemHealthCheck
{
    const int FreshDays = 3;

    public string Name => "Exchange rates";

    public int Order => 100;

    public string LogCategory => "Noof.Ledger.Host.Workers.FxRateWorker";

    public async Task<HealthOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        if (gate.State is not DatabaseState.Ready)
            return HealthOutcome.Warning("Waiting for the database");

        if (await rates.NewestAsOfDateAsync(cancellationToken) is not { } newest)
            return HealthOutcome.Warning("No exchange rates yet");

        var ageInDays = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).DayNumber - newest.DayNumber;
        return ageInDays <= FreshDays
            ? HealthOutcome.Ok($"Rates of {newest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}")
            : HealthOutcome.Warning($"Exchange rates {ageInDays} days old — normal while offline");
    }
}
