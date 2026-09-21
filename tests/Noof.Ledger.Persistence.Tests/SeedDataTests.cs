using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class SeedDataTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Exactly_one_wallet_is_seeded()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        (await db.Wallets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task The_starting_category_tree_has_top_level_and_sub_categories()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var categories = await db.Categories.ToListAsync(TestContext.Current.CancellationToken);

        categories.Count(c => c.ParentId is null).Should().BeGreaterThanOrEqualTo(15);
        categories.Should().Contain(c => c.ParentId != null,
            "the hierarchy is unproven without at least one sub-category");
    }

    [Fact]
    public async Task Coffee_lands_in_a_seeded_category()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var coffee = await db.Categories.SingleAsync(c => c.NameRu == "Кофе", TestContext.Current.CancellationToken);

        coffee.ParentId.Should().NotBeNull("coffee is a sub-category of Food & Drink, proving the hierarchy is real, not decorative");
    }
}
