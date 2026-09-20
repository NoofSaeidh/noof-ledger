using System.Globalization;
using AwesomeAssertions;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class MoneyStorageTests(PostgresFixture fixture)
{
    static readonly decimal[] Amounts = [-45.25m, 0.10m, 2.05m, 9.55m, 10.00m, 100.00m, 999.99m, 1234.50m];

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public async Task Ordering_and_aggregation_are_correct_in_every_culture(string culture)
    {
        CultureInfo.CurrentCulture = new CultureInfo(culture);

        await using var db = await fixture.CreateDatabaseAsync();
        await SeedAsync(db, Amounts);

        var ordered = await QueryDecimalsAsync(db, "SELECT amount FROM money_probe ORDER BY amount");
        var sum = await QuerySingleAsync(db, "SELECT SUM(amount) FROM money_probe");
        var max = await QuerySingleAsync(db, "SELECT MAX(amount) FROM money_probe");

        ordered.Should().BeInAscendingOrder().And.Equal([.. Amounts.Order()]);
        sum.Should().Be(Amounts.Sum());
        max.Should().Be(1234.50m);
    }

    [Fact]
    public async Task Negative_amounts_round_trip_exactly()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await SeedAsync(db, [-1234.5678m]);

        var value = await QuerySingleAsync(db, "SELECT amount FROM money_probe");

        value.Should().Be(-1234.5678m);
    }

    [Fact]
    public async Task More_than_four_decimal_places_is_rounded_not_rejected()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await SeedAsync(db, [1.00005m]);

        var value = await QuerySingleAsync(db, "SELECT amount FROM money_probe");

        value.Should().Be(1.0001m);
    }

    [Fact]
    public async Task An_amount_beyond_the_column_range_is_rejected_loudly()
    {
        await using var db = await fixture.CreateDatabaseAsync();

        var act = async () => await SeedAsync(db, [12345678901234567.89m]);

        await act.Should().ThrowAsync<PostgresException>();
    }

    static async Task SeedAsync(NpgsqlConnection db, IEnumerable<decimal> amounts)
    {
        await using (var create = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS money_probe (id serial PRIMARY KEY, amount numeric(19,4) NOT NULL)", db))
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        foreach (var amount in amounts)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO money_probe (amount) VALUES (@a)", db);
            insert.Parameters.AddWithValue("a", amount);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    static async Task<decimal[]> QueryDecimalsAsync(NpgsqlConnection db, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        var results = new List<decimal>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            results.Add(reader.GetDecimal(0));

        return [.. results];
    }

    static async Task<decimal> QuerySingleAsync(NpgsqlConnection db, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db);
        return (decimal)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
