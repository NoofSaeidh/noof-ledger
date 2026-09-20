using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

public class AppUserModelTests
{
    static LedgerDbContext BuildOfflineContext()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none")
            .Options;

        return new LedgerDbContext(options);
    }

    [Fact]
    public void Maps_to_the_app_user_table_with_snake_case_columns()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(AppUser))!;

        entity.GetTableName().Should().Be("app_user");
        entity.GetProperty(nameof(AppUser.Id)).GetColumnName().Should().Be("id");
        entity.GetProperty(nameof(AppUser.Username)).GetColumnName().Should().Be("username");
        entity.GetProperty(nameof(AppUser.PasswordHash)).GetColumnName().Should().Be("password_hash");
        entity.GetProperty(nameof(AppUser.CreatedAt)).GetColumnName().Should().Be("created_at");
    }

    [Fact]
    public void Every_column_is_required_and_bounded()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(AppUser))!;

        entity.GetProperty(nameof(AppUser.Username)).IsNullable.Should().BeFalse();
        entity.GetProperty(nameof(AppUser.Username)).GetMaxLength().Should().Be(64);
        entity.GetProperty(nameof(AppUser.PasswordHash)).IsNullable.Should().BeFalse();
        entity.GetProperty(nameof(AppUser.CreatedAt)).GetColumnType().Should().Be("timestamptz");
    }

    [Fact]
    public void The_primary_key_is_id()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(AppUser))!;

        entity.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(AppUser.Id));
    }

    [Fact]
    public void The_money_probe_mapping_is_unchanged_by_the_move_to_configuration_classes()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(MoneyProbeEntity))!;
        var money = entity.GetComplexProperties().Single();

        entity.GetTableName().Should().Be("money_probe_entities");
        money.ComplexType.FindProperty("Amount")!.GetColumnName().Should().Be("amount");
        money.ComplexType.FindProperty("Currency")!.GetMaxLength().Should().Be(3);
    }
}
