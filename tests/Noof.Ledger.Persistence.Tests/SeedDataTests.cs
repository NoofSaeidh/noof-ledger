using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Persistence.Migrations;

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

    [Fact]
    public async Task Reapplying_the_seed_insert_leaves_the_row_counts_unchanged()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var categoryCountBefore = await db.Categories.CountAsync(TestContext.Current.CancellationToken);

        var act = async () =>
        {
            await db.Database.ExecuteSqlRawAsync(AddCaptureModel.SeedTopLevelCategoriesSql, TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync(AddCaptureModel.SeedDefaultWalletSql, TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync(AddCaptureModel.SeedSubCategoriesSql, TestContext.Current.CancellationToken);
        };

        await act.Should().NotThrowAsync("the seed insert must be safe to run against a database that already has these rows");

        (await db.Categories.CountAsync(TestContext.Current.CancellationToken)).Should().Be(categoryCountBefore);
        (await db.Wallets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task An_operators_rename_survives_the_seed_insert_running_again()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var coffee = await db.Categories.SingleAsync(c => c.Slug == "coffee", TestContext.Current.CancellationToken);
        coffee.NameEn = "Espresso Bar";
        coffee.NameRu = "Эспрессо-бар";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await db.Database.ExecuteSqlRawAsync(AddCaptureModel.SeedSubCategoriesSql, TestContext.Current.CancellationToken);

        var reloaded = await db.Categories.AsNoTracking()
            .SingleAsync(c => c.Slug == "coffee", TestContext.Current.CancellationToken);

        reloaded.NameEn.Should().Be("Espresso Bar",
            "the migration mechanism running again must not overwrite an operator's rename");
        reloaded.NameRu.Should().Be("Эспрессо-бар",
            "the migration mechanism running again must not overwrite an operator's rename");
    }
}
