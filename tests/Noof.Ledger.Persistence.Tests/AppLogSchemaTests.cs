using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class AppLogSchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_table_has_exactly_the_contracts_columns()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var columns = await db.Database.SqlQueryRaw<string>(
            """
            SELECT column_name || ' ' || data_type || CASE WHEN is_nullable = 'NO' THEN ' NOT NULL' ELSE '' END AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'app_log'
            ORDER BY ordinal_position
            """).ToListAsync(TestContext.Current.CancellationToken);

        columns.Should().Equal(
            "id bigint NOT NULL",
            "logged_at timestamp with time zone NOT NULL",
            "level smallint NOT NULL",
            "source text",
            "message text NOT NULL",
            "template text NOT NULL",
            "exception text",
            "transaction_id uuid",
            "properties jsonb");
    }

    [Fact]
    public async Task Indexes_on_logged_at_transaction_id_and_level_logged_at_all_exist()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var indexes = await db.Database.SqlQueryRaw<string>(
            """
            SELECT indexname AS "Value" FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = 'app_log'
            """).ToListAsync(TestContext.Current.CancellationToken);

        indexes.Should().Contain(name => name.Contains("logged_at", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("level", StringComparison.OrdinalIgnoreCase));
        indexes.Should().Contain(name => name.Contains("transaction_id", StringComparison.OrdinalIgnoreCase));
        indexes.Should().Contain(name => name.Contains("level", StringComparison.OrdinalIgnoreCase)
            && name.Contains("logged_at", StringComparison.OrdinalIgnoreCase));
    }
}
