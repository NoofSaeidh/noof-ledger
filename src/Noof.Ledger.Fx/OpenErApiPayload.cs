using System.Text.Json.Serialization;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Fx;

// The fields of open.er-api.com's /v6/latest answer the app reads (exchangerate-api.com/docs/free). Rates are read
// straight into decimal; a number no decimal holds fails deserialisation, which the source reports as invalid JSON.
internal sealed record OpenErApiPayload(
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("base_code")] string? BaseCode,
    [property: JsonPropertyName("time_last_update_unix")] long? TimeLastUpdateUnix,
    [property: JsonPropertyName("time_eol_unix")] long? TimeEolUnix,
    [property: JsonPropertyName("rates")] Dictionary<string, decimal>? Rates)
{
    // fx_rates.units_per_eur is numeric(24,12): twelve digits on each side of the point.
    const decimal LargestStorableRate = 999_999_999_999.999_999_999_999m;

    // A local clock somewhat behind the endpoint's must not throw a good day away.
    static readonly TimeSpan ClockTolerance = TimeSpan.FromDays(1);

    // A snapshot is whole or nothing: the first failed check rejects all of it.
    public (FxRateSnapshot? Snapshot, string? Rejection) Read(DateTimeOffset now)
    {
        if (Result != "success")
            return Rejected("the result was not success");
        if (BaseCode != "EUR")
            return Rejected("the base was not EUR");
        if (TimeLastUpdateUnix is not { } updatedUnix || updatedUnix <= 0)
            return Rejected("the update time is missing");
        if (updatedUnix > (now + ClockTolerance).ToUnixTimeSeconds())
            return Rejected("the update time is in the future");
        if (Rates is not { } rates)
            return Rejected("the rates are missing");

        var unitsPerEur = new Dictionary<CurrencyCode, decimal>();
        foreach (var currency in CurrencyCode.Supported.Where(code => code != CurrencyCode.Eur))
        {
            if (!rates.TryGetValue(currency.Value, out var rate))
                return Rejected($"the {currency} rate is missing");
            if (rate <= 0m)
                return Rejected($"the {currency} rate is not above zero");

            // What PostgreSQL would store: it rounds numeric input to the column's scale, half away from zero.
            var stored = decimal.Round(rate, 12, MidpointRounding.AwayFromZero);
            if (stored == 0m || stored > LargestStorableRate)
                return Rejected($"the {currency} rate does not fit numeric(24,12)");

            unitsPerEur[currency] = rate;
        }

        var asOfDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(updatedUnix).UtcDateTime);
        return (new FxRateSnapshot(asOfDate, FxSources.OpenErApi, unitsPerEur), null);
    }

    static (FxRateSnapshot?, string?) Rejected(string reason) => (null, reason);
}
