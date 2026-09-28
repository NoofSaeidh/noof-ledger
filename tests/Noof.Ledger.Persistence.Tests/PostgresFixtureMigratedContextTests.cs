using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class PostgresFixtureMigratedContextTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CreateMigratedContextAsync_returns_a_context_with_every_migration_already_applied()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        var pending = await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);

        pending.Should().BeEmpty(
            "the template clone is already migrated, so a caller must never run MigrateAsync against it");
    }
}
