using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class MigrationContractTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_create_the_database_from_empty()
    {
        await using var db = await fixture.CreateContextAsync();

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var applied = await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        applied.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Migrating_twice_is_a_no_op()
    {
        await using var db = await fixture.CreateContextAsync();

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var pending = await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task The_model_has_no_pending_changes()
    {
        await using var db = await fixture.CreateContextAsync();

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "a model change without a migration means the next deploy silently diverges from the schema");
    }

    [Fact]
    public async Task Money_columns_are_numeric_19_4()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var type = await db.Database
            .SqlQuery<string>($"""
                SELECT data_type || '(' || numeric_precision || ',' || numeric_scale || ')' AS "Value"
                FROM information_schema.columns
                WHERE table_name = 'money_probe_entities' AND column_name = 'amount'
                """)
            .SingleAsync(TestContext.Current.CancellationToken);

        type.Should().Be("numeric(19,4)");
    }

    [Fact]
    public async Task Timestamp_columns_are_timestamptz()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var type = await db.Database
            .SqlQuery<string>($"""
                SELECT data_type AS "Value"
                FROM information_schema.columns
                WHERE table_name = 'money_probe_entities' AND column_name = 'recorded_at'
                """)
            .SingleAsync(TestContext.Current.CancellationToken);

        type.Should().Be("timestamp with time zone");
    }

    [Fact]
    public async Task Currency_column_is_not_nullable()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var isNullable = await db.Database
            .SqlQuery<string>($"""
                SELECT is_nullable AS "Value"
                FROM information_schema.columns
                WHERE table_name = 'money_probe_entities' AND column_name = 'currency'
                """)
            .SingleAsync(TestContext.Current.CancellationToken);

        isNullable.Should().Be("NO");
    }

    [Fact]
    public async Task Reading_a_corrupted_currency_value_throws_instead_of_materializing_a_default()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO money_probe_entities (amount, currency, recorded_at) VALUES (10.00, '123', now())",
            TestContext.Current.CancellationToken);

        var act = async () => await db.MoneyProbes.SingleAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
