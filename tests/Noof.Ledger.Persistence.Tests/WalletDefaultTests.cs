using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class WalletDefaultTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_second_default_wallet_is_rejected_by_the_database()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        db.Wallets.Add(new Wallet
        {
            Id = Guid.CreateVersion7(),
            Name = "Second",
            Currency = CurrencyCode.Eur,
            IsDefault = true,
        });

        var act = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(e => e.SqlState == PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_second_non_default_wallet_is_accepted()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        db.Wallets.Add(new Wallet
        {
            Id = Guid.CreateVersion7(),
            Name = "Second",
            Currency = CurrencyCode.Eur,
            IsDefault = false,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Wallets.Count(w => !w.IsDefault).Should().Be(1);
    }
}
