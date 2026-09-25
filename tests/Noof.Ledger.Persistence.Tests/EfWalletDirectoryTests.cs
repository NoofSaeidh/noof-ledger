using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Wallets;
using Npgsql;
using AppReceipts = Noof.Ledger.Application.Receipts;

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

    [Fact]
    public async Task DefaultForPaymentAsync_returns_the_wallet_marked_default_for_card()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var cardWallet = NewWallet("Card Wallet", CurrencyCode.Rsd);
        cardWallet.DefaultForPayment = WalletPaymentDefault.Card;
        db.Wallets.Add(cardWallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var walletId = await new EfWalletDirectory(db)
            .DefaultForPaymentAsync(AppReceipts.PaymentMethod.Card, TestContext.Current.CancellationToken);

        walletId.Should().Be(cardWallet.Id);
    }

    [Fact]
    public async Task DefaultForPaymentAsync_returns_null_when_no_wallet_is_marked_default_for_cash()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var walletId = await new EfWalletDirectory(db)
            .DefaultForPaymentAsync(AppReceipts.PaymentMethod.Cash, TestContext.Current.CancellationToken);

        walletId.Should().BeNull();
    }

    [Theory]
    [InlineData(AppReceipts.PaymentMethod.Transfer)]
    [InlineData(AppReceipts.PaymentMethod.Voucher)]
    [InlineData(AppReceipts.PaymentMethod.Other)]
    [InlineData(AppReceipts.PaymentMethod.Mixed)]
    public async Task DefaultForPaymentAsync_answers_null_without_a_query_for_every_method_but_card_and_cash(
        AppReceipts.PaymentMethod method)
    {
        await using var db = await fixture.CreateContextAsync();

        var walletId = await new EfWalletDirectory(db).DefaultForPaymentAsync(method, TestContext.Current.CancellationToken);

        walletId.Should().BeNull("a wallet is never the default for anything but Card or Cash (R-3)");
    }

    [Fact]
    public async Task Only_one_wallet_may_be_the_default_for_a_given_payment_method()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var first = NewWallet("First Card Wallet", CurrencyCode.Rsd);
        first.DefaultForPayment = WalletPaymentDefault.Card;
        db.Wallets.Add(first);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var second = NewWallet("Second Card Wallet", CurrencyCode.Eur);
        second.DefaultForPayment = WalletPaymentDefault.Card;
        db.Wallets.Add(second);

        var act = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<DbUpdateException>();
        assertion.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }
}
