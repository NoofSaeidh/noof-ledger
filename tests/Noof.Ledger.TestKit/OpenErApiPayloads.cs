using Noof.Ledger.Application.Fx;
using Noof.Ledger.Domain;

namespace Noof.Ledger.TestKit;

// A synthetic answer of open.er-api.com/v6/latest/EUR in the documented shape (exchangerate-api.com/docs/free): the
// four currencies the app supports besides EUR, EUR itself, and one the app ignores. Rates are raw JSON tokens, so a
// test can put a zero, a negative, a string or an out-of-range number in their place.
public static class OpenErApiPayloads
{
    // 2026-10-08 00:00:01 UTC: the endpoint publishes a day's rates just after midnight UTC.
    public const long UpdatedUnix = 1_791_417_601;

    public static IReadOnlyDictionary<string, string> Rates { get; } = new Dictionary<string, string>
    {
        ["EUR"] = "1",
        ["GBP"] = "0.8675",
        ["KZT"] = "625.4417",
        ["RSD"] = "117.1532",
        ["RUB"] = "94.3121",
        ["USD"] = "1.1612",
    };

    // The default rates with one currency's token replaced; a null token leaves the currency out.
    public static IReadOnlyDictionary<string, string> RatesWith(string currency, string? json)
    {
        var rates = new Dictionary<string, string>(Rates);
        if (json is null)
            rates.Remove(currency);
        else
            rates[currency] = json;
        return rates;
    }

    public static string Latest(
        string result = "success", string baseCode = "EUR", long? updatedUnix = UpdatedUnix, long endOfLifeUnix = 0,
        IReadOnlyDictionary<string, string>? rates = null)
    {
        var updated = updatedUnix is { } unix ? $"\"time_last_update_unix\":{unix}," : "";
        var ratesObject = "{" + string.Join(",", (rates ?? Rates).Select(rate => $"\"{rate.Key}\":{rate.Value}")) + "}";
        return $$"""{"result":"{{result}}","provider":"https://www.exchangerate-api.com","documentation":"https://www.exchangerate-api.com/docs/free","terms_of_use":"https://www.exchangerate-api.com/terms",{{updated}}"time_next_update_unix":{{(updatedUnix ?? UpdatedUnix) + 86_400}},"time_eol_unix":{{endOfLifeUnix}},"base_code":"{{baseCode}}","rates":{{ratesObject}}}""";
    }

    // What OpenErApiRateSource reads from Latest(): a test seeds this where a real fetch would have stored.
    public static FxRateSnapshot Snapshot(DateOnly asOfDate) => new(asOfDate, FxSources.OpenErApi, new Dictionary<CurrencyCode, decimal>
    {
        [CurrencyCode.Rsd] = 117.1532m,
        [CurrencyCode.Usd] = 1.1612m,
        [CurrencyCode.Rub] = 94.3121m,
        [CurrencyCode.Kzt] = 625.4417m,
    });
}
