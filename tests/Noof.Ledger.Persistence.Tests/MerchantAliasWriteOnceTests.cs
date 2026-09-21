using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class MerchantAliasWriteOnceTests(PostgresFixture fixture)
{
    static async Task<LedgerDbContext> SeedAliasAsync(PostgresFixture fixture)
    {
        var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var merchant = new Merchant { Id = Guid.NewGuid(), DisplayName = "Test Merchant", Kind = MerchantKind.Retail };
        db.Merchants.Add(merchant);
        db.MerchantAliases.Add(new MerchantAlias
        {
            Folded = "TEST MERCHANT",
            MerchantId = merchant.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return db;
    }

    [Fact]
    public async Task Updating_an_existing_alias_is_rejected_by_the_database()
    {
        await using var db = await SeedAliasAsync(fixture);

        var act = async () => await db.Database.ExecuteSqlAsync(
            $"UPDATE merchant_aliases SET merchant_id = merchant_id WHERE folded = 'TEST MERCHANT'",
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PostgresException>(
            "merchant_aliases is write-once; even a raw SQL UPDATE must be rejected by the database itself");
    }

    [Fact]
    public async Task Deleting_an_existing_alias_is_rejected_by_the_database()
    {
        await using var db = await SeedAliasAsync(fixture);

        var act = async () => await db.Database.ExecuteSqlAsync(
            $"DELETE FROM merchant_aliases WHERE folded = 'TEST MERCHANT'",
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<PostgresException>(
            "merchant_aliases is append-only; deleting history must be impossible even by direct SQL");
    }
}
