using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class WalletDefaultTests(PostgresFixture fixture)
{
    static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");

    static Wallet NewWallet(CurrencyCode currency, bool isDefaultForCurrency, params string[] aliases) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "Second",
        Currency = currency,
        IsDefaultForCurrency = isDefaultForCurrency,
        Aliases = aliases,
    };

    [Fact]
    public async Task The_seeded_main_wallet_is_the_RSD_default()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var main = await db.Wallets.AsNoTracking().SingleAsync(w => w.Id == MainWalletId, TestContext.Current.CancellationToken);

        main.Currency.Should().Be(CurrencyCode.Rsd);
        main.IsDefaultForCurrency.Should().BeTrue("the one global default of Phases 1-3 becomes the RSD default, not a wallet with none");
        main.Archived.Should().BeFalse();
        main.Aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_default_for_the_same_currency_is_rejected_by_the_database()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Wallets.Add(NewWallet(CurrencyCode.Rsd, isDefaultForCurrency: true));

        var act = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(e => e.SqlState == PostgresErrorCodes.UniqueViolation
                && e.ConstraintName == WalletConfiguration.OneDefaultPerCurrencyIndex);
    }

    [Fact]
    public async Task A_default_for_another_currency_is_accepted()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Wallets.Add(NewWallet(CurrencyCode.Eur, isDefaultForCurrency: true));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await db.Wallets.CountAsync(w => w.IsDefaultForCurrency, TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task A_second_non_default_wallet_in_the_same_currency_is_accepted()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Wallets.Add(NewWallet(CurrencyCode.Rsd, isDefaultForCurrency: false));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await db.Wallets.CountAsync(w => !w.IsDefaultForCurrency, TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task A_wallet_keeps_its_aliases_in_order_and_is_stamped_when_it_is_created()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Rsd, isDefaultForCurrency: false, "райф", "raiffeisen");
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var stored = await db.Wallets.AsNoTracking().SingleAsync(w => w.Id == wallet.Id, TestContext.Current.CancellationToken);

        stored.Aliases.Should().Equal("райф", "raiffeisen");
        stored.Archived.Should().BeFalse();
        stored.CreatedAt.Should().BeAfter(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "a wallet saved without CreatedAt is stamped by the database, not left at year 1");
    }
}
