using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Noof.Ledger.Persistence.Fx;

// Append-only (spec SS-2): a day already stored keeps its first rates, so a repeat fetch inserts nothing and no stored
// rate is ever superseded. One statement per snapshot, so a snapshot is stored whole or not at all.
internal sealed class EfFxRateStore(LedgerDbContext db, TimeProvider timeProvider) : IFxRateStore
{
    const string AppendSql = """
        INSERT INTO fx_rates (currency, as_of_date, source, units_per_eur, fetched_at)
        SELECT rate.currency, @asOfDate, @source, rate.units_per_eur, @fetchedAt
        FROM unnest(@currencies, @unitsPerEur) AS rate(currency, units_per_eur)
        ON CONFLICT DO NOTHING
        """;

    // A day inside the range may need the rate from just before it, and a day with nothing earlier takes the first one
    // after it (RateTable), so the nearest rate on each side comes along per currency.
    const string RangeSql = """
        SELECT r.currency AS "Currency", r.as_of_date AS "AsOfDate", r.units_per_eur AS "UnitsPerEur"
        FROM fx_rates r
        WHERE r.source = @source AND r.as_of_date BETWEEN @from AND @to
        UNION ALL
        (SELECT DISTINCT ON (r.currency) r.currency, r.as_of_date, r.units_per_eur
         FROM fx_rates r
         WHERE r.source = @source AND r.as_of_date < @from
         ORDER BY r.currency, r.as_of_date DESC)
        UNION ALL
        (SELECT DISTINCT ON (r.currency) r.currency, r.as_of_date, r.units_per_eur
         FROM fx_rates r
         WHERE r.source = @source AND r.as_of_date > @to
         ORDER BY r.currency, r.as_of_date)
        """;

    public async Task<bool> AppendAsync(FxRateSnapshot snapshot, CancellationToken cancellationToken)
    {
        var rates = snapshot.UnitsPerEur.OrderBy(rate => rate.Key).ToList();

        var inserted = await db.Database.ExecuteSqlRawAsync(
            AppendSql,
            [
                new NpgsqlParameter("asOfDate", snapshot.AsOfDate),
                new NpgsqlParameter("source", snapshot.Source),
                new NpgsqlParameter("fetchedAt", timeProvider.GetUtcNow()),
                new NpgsqlParameter("currencies", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
                {
                    Value = rates.Select(rate => rate.Key.Value).ToArray(),
                },
                new NpgsqlParameter("unitsPerEur", NpgsqlDbType.Array | NpgsqlDbType.Numeric)
                {
                    Value = rates.Select(rate => rate.Value).ToArray(),
                },
            ],
            cancellationToken);

        return inserted > 0;
    }

    public async Task<IReadOnlyList<FxRate>> GetAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<FxRateSqlRow>(
                RangeSql,
                new NpgsqlParameter("source", FxSources.OpenErApi),
                new NpgsqlParameter("from", from),
                new NpgsqlParameter("to", to))
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .Select(row => new FxRate(new CurrencyCode(row.Currency), row.AsOfDate, row.UnitsPerEur))
                .OrderBy(rate => rate.Currency)
                .ThenBy(rate => rate.AsOfDate),
        ];
    }

    public Task<DateOnly?> NewestAsOfDateAsync(CancellationToken cancellationToken) =>
        db.FxRates.AsNoTracking()
            .Where(rate => rate.Source == FxSources.OpenErApi)
            .MaxAsync(rate => (DateOnly?)rate.AsOfDate, cancellationToken);
}

internal sealed class FxRateSqlRow
{
    public string Currency { get; init; } = string.Empty;
    public DateOnly AsOfDate { get; init; }
    public decimal UnitsPerEur { get; init; }
}
