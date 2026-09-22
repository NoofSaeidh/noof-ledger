using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

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
}
