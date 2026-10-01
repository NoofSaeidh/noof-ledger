using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Wallets;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfWalletFxTermsTests(PostgresFixture fixture)
{
    static readonly DateTimeOffset Created = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    static readonly WalletTermsDetails DollarsAtKaspi = new(CurrencyCode.Usd, 520m, 1m, null, null);

    static Wallet NewWallet(string name, CurrencyCode currency) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Currency = currency,
        CreatedAt = Created,
    };

    async Task<LedgerDbContext> WithWalletsAsync(params Wallet[] wallets)
    {
        var db = await fixture.CreateMigratedContextAsync();
        db.Wallets.AddRange(wallets);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        return db;
    }

    [Fact]
    public async Task ListAsync_is_empty_for_a_wallet_with_no_terms()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        await using var db = await WithWalletsAsync(kaspi);

        var terms = await new EfWalletFxTerms(db).ListAsync(kaspi.Id, cancellationToken);

        terms.Should().BeEmpty();
    }

    [Fact]
    public async Task SetAsync_adds_terms_that_ListAsync_returns_by_currency_for_that_wallet_only()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        var raiffeisen = NewWallet("Raiffeisen RSD", CurrencyCode.Rsd);
        await using var db = await WithWalletsAsync(kaspi, raiffeisen);
        var store = new EfWalletFxTerms(db);

        await store.SetAsync(kaspi.Id, DollarsAtKaspi, cancellationToken);
        await store.SetAsync(kaspi.Id, new(CurrencyCode.Rub, 5.6m, null, 150.00m, null), cancellationToken);
        await store.SetAsync(kaspi.Id, new(CurrencyCode.Eur, 565.25m, 1.5m, null, 500.00m), cancellationToken);
        await store.SetAsync(raiffeisen.Id, new(CurrencyCode.Eur, 117.35m, null, null, null), cancellationToken);

        (await store.ListAsync(kaspi.Id, cancellationToken)).Should().Equal(
            new WalletTermsDetails(CurrencyCode.Eur, 565.25m, 1.5m, null, 500.00m),
            new WalletTermsDetails(CurrencyCode.Rub, 5.6m, null, 150.00m, null),
            DollarsAtKaspi);
        (await store.ListAsync(raiffeisen.Id, cancellationToken)).Should().Equal(
            new WalletTermsDetails(CurrencyCode.Eur, 117.35m, null, null, null));
    }

    [Fact]
    public async Task SetAsync_replaces_every_field_of_terms_already_set_for_that_currency()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        await using var db = await WithWalletsAsync(kaspi);
        var store = new EfWalletFxTerms(db);
        await store.SetAsync(kaspi.Id, new(CurrencyCode.Usd, 520m, 1m, 50.00m, 100.00m), cancellationToken);

        await store.SetAsync(kaspi.Id, new(CurrencyCode.Usd, 515.5m, null, null, 200.00m), cancellationToken);

        (await store.ListAsync(kaspi.Id, cancellationToken)).Should().Equal(
            [new WalletTermsDetails(CurrencyCode.Usd, 515.5m, null, null, 200.00m)],
            "a fee the new terms leave empty is cleared, not kept from the old ones");
    }

    [Fact]
    public async Task SetAsync_keeps_a_rate_to_twelve_decimals_and_a_fee_percent_to_four()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var raiffeisen = NewWallet("Raiffeisen RSD", CurrencyCode.Rsd);
        await using var db = await WithWalletsAsync(raiffeisen);
        var store = new EfWalletFxTerms(db);
        var precise = new WalletTermsDetails(CurrencyCode.Eur, 117.123456789012m, 1.2345m, 0.0001m, 99.9999m);

        await store.SetAsync(raiffeisen.Id, precise, cancellationToken);

        (await store.ListAsync(raiffeisen.Id, cancellationToken)).Should().Equal(precise);
    }

    [Fact]
    public async Task RemoveAsync_deletes_that_currency_only()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        await using var db = await WithWalletsAsync(kaspi);
        var store = new EfWalletFxTerms(db);
        await store.SetAsync(kaspi.Id, DollarsAtKaspi, cancellationToken);
        await store.SetAsync(kaspi.Id, new(CurrencyCode.Eur, 565.25m, null, null, null), cancellationToken);

        await store.RemoveAsync(kaspi.Id, CurrencyCode.Eur, cancellationToken);

        (await store.ListAsync(kaspi.Id, cancellationToken)).Should().Equal(DollarsAtKaspi);
    }

    [Fact]
    public async Task RemoveAsync_of_terms_that_were_never_set_does_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        await using var db = await WithWalletsAsync(kaspi);
        var store = new EfWalletFxTerms(db);
        await store.SetAsync(kaspi.Id, DollarsAtKaspi, cancellationToken);

        await store.RemoveAsync(kaspi.Id, CurrencyCode.Rub, cancellationToken);
        await store.RemoveAsync(Guid.NewGuid(), CurrencyCode.Usd, cancellationToken);

        (await store.ListAsync(kaspi.Id, cancellationToken)).Should().Equal(DollarsAtKaspi);
    }

    [Fact]
    public async Task Terms_can_be_set_again_on_the_same_context_after_they_were_removed()
    {
        // The /wallets page keeps one context for its whole circuit: add, remove, add back is an ordinary afternoon.
        var cancellationToken = TestContext.Current.CancellationToken;
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        await using var db = await WithWalletsAsync(kaspi);
        var store = new EfWalletFxTerms(db);
        await store.SetAsync(kaspi.Id, DollarsAtKaspi, cancellationToken);
        await store.RemoveAsync(kaspi.Id, CurrencyCode.Usd, cancellationToken);

        await store.SetAsync(kaspi.Id, new(CurrencyCode.Usd, 530m, 1m, null, null), cancellationToken);

        (await store.ListAsync(kaspi.Id, cancellationToken)).Should().Equal(
            new WalletTermsDetails(CurrencyCode.Usd, 530m, 1m, null, null));
    }

    [Fact]
    public async Task Changing_or_removing_a_wallets_terms_never_changes_a_charge_already_recorded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        await using var db = await WithWalletsAsync(kaspi);
        var store = new EfWalletFxTerms(db);
        await store.SetAsync(kaspi.Id, DollarsAtKaspi, cancellationToken);

        var purchase = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = kaspi.Id,
            Kind = TransactionKind.Expense,
            RawText = "30 долларов с каспи",
            Status = TransactionStatus.Completed,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = Created,
            OccurredOn = new DateOnly(2026, 10, 1),
            TelegramChatId = 1,
            TelegramMessageId = 71_001,
            CreatedAt = Created,
        };
        db.Transactions.Add(purchase);
        await db.SaveChangesAsync(cancellationToken);
        db.Charges.Add(new Charge
        {
            TransactionId = purchase.Id,
            Currency = CurrencyCode.Usd,
            ChargedAmount = 15600.00m,
            FeeAmount = 156.00m,
            RateUsed = 520m,
            FeePercent = 1m,
            FeeFixed = null,
            FeeMinimum = null,
            Source = ChargeSource.WalletTerms,
        });
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        await store.SetAsync(kaspi.Id, new(CurrencyCode.Usd, 540m, 2m, 100.00m, null), cancellationToken);
        await TheChargeIsAsRecordedAsync();

        await store.RemoveAsync(kaspi.Id, CurrencyCode.Usd, cancellationToken);
        await TheChargeIsAsRecordedAsync();

        async Task TheChargeIsAsRecordedAsync()
        {
            var charge = await db.Charges.AsNoTracking()
                .SingleAsync(c => c.TransactionId == purchase.Id, cancellationToken);

            charge.ChargedAmount.Should().Be(15600.00m, "the terms store never writes to charges");
            charge.FeeAmount.Should().Be(156.00m);
            charge.RateUsed.Should().Be(520m);
            charge.FeePercent.Should().Be(1m);
            charge.FeeFixed.Should().BeNull();
            charge.FeeMinimum.Should().BeNull();
            charge.Source.Should().Be(ChargeSource.WalletTerms);
        }
    }

    public static TheoryData<string, decimal, decimal?, decimal?, decimal?> OutOfRangeTerms => new()
    {
        { "a zero rate", 0m, null, null, null },
        { "a negative rate", -117.35m, null, null, null },
        { "a negative fee percent", 117.35m, -0.5m, null, null },
        { "a negative fixed fee", 117.35m, null, -50.00m, null },
        { "a negative minimum fee", 117.35m, null, null, -100.00m },
    };

    [Fact]
    public async Task SetAsync_given_an_unknown_wallet_throws_and_writes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await fixture.CreateMigratedContextAsync();

        var act = () => new EfWalletFxTerms(db).SetAsync(Guid.NewGuid(), DollarsAtKaspi, cancellationToken);

        await act.Should().ThrowExactlyAsync<KeyNotFoundException>();
        (await db.WalletFxTerms.AsNoTracking().CountAsync(cancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task SetAsync_in_the_wallets_own_currency_throws_and_writes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        await using var db = await WithWalletsAsync(kaspi);
        var store = new EfWalletFxTerms(db);

        var act = () => store.SetAsync(kaspi.Id, new(CurrencyCode.Kzt, 1m, null, null, null), cancellationToken);

        await act.Should().ThrowExactlyAsync<ArgumentException>(
            "a spending in the wallet's own currency is never charged, so it has no terms");
        (await store.ListAsync(kaspi.Id, cancellationToken)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(OutOfRangeTerms))]
    public async Task SetAsync_refuses_a_rate_that_is_not_positive_or_a_negative_fee_and_writes_nothing(
        string why, decimal rate, decimal? feePercent, decimal? feeFixed, decimal? feeMinimum)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var raiffeisen = NewWallet("Raiffeisen RSD", CurrencyCode.Rsd);
        await using var db = await WithWalletsAsync(raiffeisen);
        var store = new EfWalletFxTerms(db);

        var act = () => store.SetAsync(
            raiffeisen.Id, new(CurrencyCode.Eur, rate, feePercent, feeFixed, feeMinimum), cancellationToken);

        await act.Should().ThrowExactlyAsync<ArgumentOutOfRangeException>(why);
        (await store.ListAsync(raiffeisen.Id, cancellationToken)).Should().BeEmpty();
    }
}
