using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Noof.Ledger.Persistence.Wallets;
using Npgsql;

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
        await using var db = await fixture.CreateMigratedContextAsync();
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

    [Fact]
    public async Task DefaultForPaymentAsync_returns_the_wallet_marked_default_for_card()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var cardWallet = NewWallet("Card Wallet", CurrencyCode.Rsd);
        cardWallet.DefaultForPayment = WalletPaymentDefault.Card;
        db.Wallets.Add(cardWallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var walletId = await new EfWalletDirectory(db)
            .DefaultForPaymentAsync(PaymentMethod.Card, CurrencyCode.Rsd, TestContext.Current.CancellationToken);

        walletId.Should().Be(cardWallet.Id);
    }

    [Fact]
    public async Task DefaultForPaymentAsync_ignores_an_archived_wallet()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var archivedCardWallet = NewWallet("Closed Card Wallet", CurrencyCode.Rsd, archived: true);
        archivedCardWallet.DefaultForPayment = WalletPaymentDefault.Card;
        db.Wallets.Add(archivedCardWallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var walletId = await new EfWalletDirectory(db)
            .DefaultForPaymentAsync(PaymentMethod.Card, CurrencyCode.Rsd, TestContext.Current.CancellationToken);

        walletId.Should().BeNull(
            "an archived wallet is hidden from capture; receipt categorization must fall through to the default wallet (R-3)");
    }

    [Fact]
    public async Task DefaultForPaymentAsync_returns_null_when_no_wallet_is_marked_default_for_cash()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        var walletId = await new EfWalletDirectory(db)
            .DefaultForPaymentAsync(PaymentMethod.Cash, CurrencyCode.Rsd, TestContext.Current.CancellationToken);

        walletId.Should().BeNull();
    }

    [Theory]
    [InlineData(PaymentMethod.Transfer)]
    [InlineData(PaymentMethod.Voucher)]
    [InlineData(PaymentMethod.Other)]
    [InlineData(PaymentMethod.Mixed)]
    public async Task DefaultForPaymentAsync_answers_null_without_a_query_for_every_method_but_card_and_cash(
        PaymentMethod method)
    {
        await using var db = await fixture.CreateContextAsync();

        var walletId = await new EfWalletDirectory(db).DefaultForPaymentAsync(method, CurrencyCode.Rsd, TestContext.Current.CancellationToken);

        walletId.Should().BeNull("a wallet is never the default for anything but Card or Cash (R-3)");
    }

    [Fact]
    public async Task Wallets_of_different_currencies_may_each_be_the_default_for_the_same_payment_method()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var dinars = NewWallet("Cash RSD", CurrencyCode.Rsd);
        dinars.DefaultForPayment = WalletPaymentDefault.Cash;
        var euros = NewWallet("Cash EUR", CurrencyCode.Eur);
        euros.DefaultForPayment = WalletPaymentDefault.Cash;
        db.Wallets.AddRange(dinars, euros);

        var act = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("the Card and Cash defaults are per currency (T-13)");
    }

    [Fact]
    public async Task Only_one_wallet_per_currency_may_be_the_default_for_a_given_payment_method()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var first = NewWallet("First Card Wallet", CurrencyCode.Rsd);
        first.DefaultForPayment = WalletPaymentDefault.Card;
        db.Wallets.Add(first);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var second = NewWallet("Second Card Wallet", CurrencyCode.Rsd);
        second.DefaultForPayment = WalletPaymentDefault.Card;
        db.Wallets.Add(second);

        var act = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(e => e.SqlState == PostgresErrorCodes.UniqueViolation
                && e.ConstraintName == WalletConfiguration.OneDefaultPerPaymentMethodIndex);
    }

    [Fact]
    public async Task DefaultForPaymentAsync_answers_each_currency_with_its_own_default()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var dinars = NewWallet("Cash RSD", CurrencyCode.Rsd);
        dinars.DefaultForPayment = WalletPaymentDefault.Cash;
        var euros = NewWallet("Cash EUR", CurrencyCode.Eur);
        euros.DefaultForPayment = WalletPaymentDefault.Cash;
        db.Wallets.AddRange(dinars, euros);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var directory = new EfWalletDirectory(db);

        (await directory.DefaultForPaymentAsync(PaymentMethod.Cash, CurrencyCode.Rsd, TestContext.Current.CancellationToken))
            .Should().Be(dinars.Id);
        (await directory.DefaultForPaymentAsync(PaymentMethod.Cash, CurrencyCode.Eur, TestContext.Current.CancellationToken))
            .Should().Be(euros.Id);
    }

    [Fact]
    public async Task DefaultForPaymentAsync_ignores_the_default_of_another_currency()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var euroCard = NewWallet("Wise EUR", CurrencyCode.Eur);
        euroCard.DefaultForPayment = WalletPaymentDefault.Card;
        db.Wallets.Add(euroCard);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var walletId = await new EfWalletDirectory(db)
            .DefaultForPaymentAsync(PaymentMethod.Card, CurrencyCode.Rsd, TestContext.Current.CancellationToken);

        walletId.Should().BeNull("a dinar receipt must fall through to the RSD default wallet, never land on a euro card");
    }

    [Fact]
    public async Task Active_tells_which_wallet_is_a_payment_default()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var cashRsd = NewWallet("Cash RSD", CurrencyCode.Rsd);
        cashRsd.DefaultForPayment = WalletPaymentDefault.Cash;
        var raiffeisen = NewWallet("Raiffeisen RSD", CurrencyCode.Rsd);
        raiffeisen.DefaultForPayment = WalletPaymentDefault.Card;
        db.Wallets.AddRange(cashRsd, raiffeisen);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var active = await new EfWalletDirectory(db).ActiveAsync(TestContext.Current.CancellationToken);

        active.Single(wallet => wallet.Id == cashRsd.Id).DefaultForPayment.Should().Be(WalletPaymentDefault.Cash);
        active.Single(wallet => wallet.Id == raiffeisen.Id).DefaultForPayment.Should().Be(WalletPaymentDefault.Card);
        active.Single(wallet => wallet.Id == SeededMainWalletId).DefaultForPayment.Should().BeNull();
    }
}
