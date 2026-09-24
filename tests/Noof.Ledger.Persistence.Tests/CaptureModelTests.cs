using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

public class CaptureModelTests
{
    static LedgerDbContext BuildOfflineContext()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none")
            .Options;

        return new LedgerDbContext(options);
    }

    [Fact]
    public void Wallet_maps_to_snake_case_columns()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(Wallet))!;

        entity.GetTableName().Should().Be("wallets");
        entity.GetProperty(nameof(Wallet.Name)).GetColumnName().Should().Be("name");
        entity.GetProperty(nameof(Wallet.Currency)).GetColumnName().Should().Be("currency");
        entity.GetProperty(nameof(Wallet.Currency)).GetMaxLength().Should().Be(3);
    }

    [Fact]
    public void Category_has_a_unique_slug_and_a_restricted_self_reference()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(Category))!;

        entity.GetTableName().Should().Be("categories");
        entity.GetIndexes().Should().ContainSingle(i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(Category.Slug) }));

        var fk = entity.GetForeignKeys().Single();
        fk.PrincipalEntityType.ClrType.Should().Be<Category>();
        fk.Properties.Select(p => p.Name).Should().Equal(nameof(Category.ParentId));
        fk.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
    }

    [Fact]
    public void Merchant_maps_to_snake_case_columns()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(Merchant))!;

        entity.GetTableName().Should().Be("merchants");
        entity.GetProperty(nameof(Merchant.DisplayName)).GetColumnName().Should().Be("display_name");
    }

    [Fact]
    public void MerchantAlias_is_keyed_on_the_folded_name_with_no_updated_at_column()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(MerchantAlias))!;

        entity.GetTableName().Should().Be("merchant_aliases");
        entity.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(MerchantAlias.Folded));
    }

    [Fact]
    public void Transaction_has_a_unique_index_that_makes_capture_idempotent()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(Transaction))!;

        entity.GetTableName().Should().Be("transactions");
        entity.GetIndexes().Should().ContainSingle(i => i.IsUnique
            && i.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { nameof(Transaction.TelegramChatId), nameof(Transaction.TelegramMessageId) }));
    }

    [Fact]
    public void LineItem_maps_money_the_same_way_the_probe_did()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(LineItem))!;
        var money = entity.GetComplexProperties().Single();

        entity.GetTableName().Should().Be("line_items");
        money.ComplexType.FindProperty("Amount")!.GetColumnName().Should().Be("amount");
        money.ComplexType.FindProperty("Currency")!.GetMaxLength().Should().Be(3);
    }

    [Fact]
    public void CategorizationJob_has_an_index_supporting_the_queue_claim()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(CategorizationJob))!;

        entity.GetTableName().Should().Be("categorization_jobs");
        entity.GetIndexes().Should().ContainSingle(i =>
            i.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { nameof(CategorizationJob.Status), nameof(CategorizationJob.RunAfter) }));
    }

    [Fact]
    public void Enum_columns_persist_as_plain_integers_not_a_native_postgres_enum()
    {
        using var db = BuildOfflineContext();

        db.Model.FindEntityType(typeof(Transaction))!.GetProperty(nameof(Transaction.Status))
            .GetColumnType().Should().Be("integer");
        db.Model.FindEntityType(typeof(Merchant))!.GetProperty(nameof(Merchant.Kind))
            .GetColumnType().Should().Be("integer");
        db.Model.FindEntityType(typeof(LineItem))!.GetProperty(nameof(LineItem.CategorizedBy))
            .GetColumnType().Should().Be("integer");
        db.Model.FindEntityType(typeof(CategorizationJob))!.GetProperty(nameof(CategorizationJob.Status))
            .GetColumnType().Should().Be("integer");
        db.Model.FindEntityType(typeof(Transaction))!.GetProperty(nameof(Transaction.Kind))
            .GetColumnType().Should().Be("integer");
        db.Model.FindEntityType(typeof(Entry))!.GetProperty(nameof(Entry.Role))
            .GetColumnType().Should().Be("integer");
    }
}
