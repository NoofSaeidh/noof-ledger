using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Persistence.Secrets;

namespace Noof.Ledger.Persistence.Tests;

public class AppSecretModelTests
{
    static LedgerDbContext BuildOfflineContext()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none")
            .Options;

        return new LedgerDbContext(options);
    }

    [Fact]
    public void Maps_to_the_app_secret_table_with_snake_case_columns()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(AppSecret))!;

        entity.GetTableName().Should().Be("app_secret");
        entity.GetProperty(nameof(AppSecret.Key)).GetColumnName().Should().Be("key");
        entity.GetProperty(nameof(AppSecret.Ciphertext)).GetColumnName().Should().Be("ciphertext");
        entity.GetProperty(nameof(AppSecret.UpdatedAt)).GetColumnName().Should().Be("updated_at");
    }

    [Fact]
    public void The_primary_key_is_the_secret_key_itself()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(AppSecret))!;

        entity.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(AppSecret.Key));
    }

    [Fact]
    public void Ciphertext_and_updated_at_are_required()
    {
        using var db = BuildOfflineContext();
        var entity = db.Model.FindEntityType(typeof(AppSecret))!;

        entity.GetProperty(nameof(AppSecret.Ciphertext)).IsNullable.Should().BeFalse();
        entity.GetProperty(nameof(AppSecret.UpdatedAt)).GetColumnType().Should().Be("timestamptz");
    }
}
