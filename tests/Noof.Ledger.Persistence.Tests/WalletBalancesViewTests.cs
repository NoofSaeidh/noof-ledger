using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class WalletBalancesViewTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_view_has_exactly_the_columns_the_contract_names()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var columns = await db.Database.SqlQueryRaw<string>(
            """
            SELECT column_name || ' ' || data_type
                   || COALESCE('(' || character_maximum_length || ')', '')
                   || COALESCE('(' || numeric_precision || ',' || numeric_scale || ')', '') AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'wallet_balances'
            ORDER BY ordinal_position
            """).ToListAsync(TestContext.Current.CancellationToken);

        columns.Should().Equal("wallet_id uuid", "currency character varying(3)", "balance numeric(19,4)", "checked_on date");
    }

    [Fact]
    public async Task Migrating_down_past_it_drops_the_view_and_migrating_up_restores_it()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        // The full timestamped id, as MigrationContractTests names its targets, read from the assembly rather than
        // retyped: Task 1 chose the timestamp.
        var addMoneyModel = db.Database.GetMigrations().Single(id => id.EndsWith("_AddMoneyModel", StringComparison.Ordinal));

        await db.GetService<IMigrator>().MigrateAsync(addMoneyModel, TestContext.Current.CancellationToken);
        (await ViewCountAsync(db)).Should().Be(0);

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        (await ViewCountAsync(db)).Should().Be(1);
    }

    static async Task<int> ViewCountAsync(LedgerDbContext db) =>
        (await db.Database.SqlQueryRaw<int>(
            """
            SELECT count(*)::int AS "Value"
            FROM information_schema.views
            WHERE table_schema = 'public' AND table_name = 'wallet_balances'
            """).ToListAsync(TestContext.Current.CancellationToken)).Single();
}
