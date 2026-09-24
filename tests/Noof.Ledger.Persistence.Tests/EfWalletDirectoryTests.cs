using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Wallets;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfWalletDirectoryTests(PostgresFixture fixture)
{
    static readonly Guid SeededMainWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly DateTimeOffset Created = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    static Wallet NewWallet(
        string name, CurrencyCode currency, bool isDefaultForCurrency = false, bool archived = false, string[]? aliases = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Currency = currency,
        Aliases = aliases ?? [],
        IsDefaultForCurrency = isDefaultForCurrency,
        Archived = archived,
        CreatedAt = Created,
    };

    [Fact]
    public async Task Active_offers_every_wallet_that_is_not_archived_ordered_by_name()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wise = NewWallet("Wise EUR", CurrencyCode.Eur, isDefaultForCurrency: true, aliases: ["wise", "вайз"]);
        var cash = NewWallet("Cash", CurrencyCode.Rsd, aliases: ["налик"]);
        // Named to sort first, so a directory that forgot the archived filter fails on the order as well.
        var closed = NewWallet("Alpha Bank (closed)", CurrencyCode.Rsd, archived: true);
        db.Wallets.AddRange(wise, cash, closed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var active = await new EfWalletDirectory(db).ActiveAsync(TestContext.Current.CancellationToken);

        active.Select(wallet => wallet.Name).Should().Equal(["Cash", "Main Wallet", "Wise EUR"],
            "an archived wallet is hidden from capture (M4), and the rest come by name");
        var offered = active.Single(wallet => wallet.Id == wise.Id);
        offered.Currency.Should().Be(CurrencyCode.Eur);
        offered.Aliases.Should().Equal(["wise", "вайз"]);
        offered.IsDefaultForCurrency.Should().BeTrue();
        active.Single(wallet => wallet.Id == cash.Id).IsDefaultForCurrency.Should().BeFalse();
        active.Single(wallet => wallet.Id == SeededMainWalletId).IsDefaultForCurrency
            .Should().BeTrue("the seeded Main Wallet stays the RSD default");
    }
}
