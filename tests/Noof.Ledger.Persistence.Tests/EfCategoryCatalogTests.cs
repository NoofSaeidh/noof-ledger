using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfCategoryCatalogTests(PostgresFixture fixture)
{
    static Category NewCategory(bool isActive, Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(),
        ParentId = parentId,
        Slug = $"test-{Guid.NewGuid():N}",
        NameEn = "Test category",
        NameRu = "Тестовая категория",
        IsActive = isActive,
    };

    [Fact]
    public async Task Active_returns_only_active_categories_with_the_parents_slug_resolved()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var parent = NewCategory(isActive: true);
        var child = NewCategory(isActive: true, parentId: parent.Id);
        var inactive = NewCategory(isActive: false);
        db.Categories.AddRange(parent, child, inactive);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var catalog = new EfCategoryCatalog(db);

        var active = await catalog.ActiveAsync(TestContext.Current.CancellationToken);

        active.Should().NotContain(c => c.Id == inactive.Id, "an inactive category must never be offered to the model");
        var parentEntry = active.Single(c => c.Id == parent.Id);
        parentEntry.ParentSlug.Should().BeNull("a top-level category has no parent");
        var childEntry = active.Single(c => c.Id == child.Id);
        childEntry.ParentSlug.Should().Be(parent.Slug, "the model is offered the parent's stable slug, not its Guid");
    }
}
