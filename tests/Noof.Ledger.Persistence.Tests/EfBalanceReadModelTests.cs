using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Balances;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfBalanceReadModelTests(PostgresFixture fixture)
{
    static DateOnly On(int day) => new(2026, 9, day);

    static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 9, day, hour, minute, 0, TimeSpan.Zero);

    async Task<LedgerDbContext> MigratedAsync()
    {
        var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return db;
    }

    static async Task<IReadOnlyList<Money>> SaveAndReadAsync(LedgerDbContext db, Wallet wallet)
    {
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return await new EfBalanceReadModel(db).BalanceOfAsync(wallet.Id, TestContext.Current.CancellationToken);
    }

    static async Task<DateOnly?> LastCheckedOnAsync(LedgerDbContext db, Wallet wallet) =>
        (await new EfBalanceReadModel(db).BalancesAsync(TestContext.Current.CancellationToken))
            .Single(balance => balance.WalletId == wallet.Id).LastCheckedOn;

    [Fact]
    public async Task With_no_checkpoint_the_balance_is_the_sum_of_the_entries()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100.10m, On(2), At(2, 9));
        BalanceSeed.Spend(db, wallet, 0.20m, On(3), At(3, 9));
        BalanceSeed.Earn(db, wallet, 1000.00m, On(4), At(4, 9));

        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(899.70m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task A_checkpoint_re_anchors_the_balance_and_entries_before_it_stop_counting()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 300m, On(2), At(2, 9));
        BalanceSeed.State(db, wallet, 5000m, On(5), At(5, 9));
        BalanceSeed.Spend(db, wallet, 200m, On(6), At(6, 9));
        BalanceSeed.Earn(db, wallet, 50m, On(7), At(7, 9));

        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(4850m, CurrencyCode.Rsd));
        (await LastCheckedOnAsync(db, wallet)).Should().Be(On(5));
    }

    [Fact]
    public async Task Processing_order_never_changes_a_balance_only_the_ledger_order_does()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.State(db, wallet, 1000m, On(10), At(10, 9));
        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(1000m, CurrencyCode.Rsd));

        var processedLater = At(12, 18);
        // An earlier day, recorded after the statement.
        BalanceSeed.Spend(db, wallet, 100m, On(9), At(9, 18), createdAt: processedLater);
        // "вчера купил…" sent after the statement: the purchase's own day sorts first.
        BalanceSeed.Spend(db, wallet, 40m, On(9), At(10, 13), createdAt: processedLater);
        // A voice note sent at 08:59 and transcribed after the 09:00 statement.
        BalanceSeed.Spend(db, wallet, 30m, On(10), At(10, 8, 59), createdAt: processedLater);
        // Exactly the statement's own moment: already inside the stated amount.
        BalanceSeed.Spend(db, wallet, 25m, On(10), At(10, 9), createdAt: processedLater);

        (await SaveAndReadAsync(db, wallet)).Should().ContainSingle().Which.Should().Be(
            new Money(1000m, CurrencyCode.Rsd),
            "each of these sorts at or before the statement by (occurred_on, occurred_at), however late it was processed");

        BalanceSeed.Spend(db, wallet, 20m, On(10), At(10, 9, 1), createdAt: processedLater);
        BalanceSeed.Spend(db, wallet, 10m, On(11), At(11, 9), createdAt: processedLater);

        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(970m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task Of_two_statements_at_the_same_moment_the_one_recorded_later_wins()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.State(db, wallet, 700m, On(5), At(5, 9), createdAt: At(5, 9, 5));
        BalanceSeed.State(db, wallet, 500m, On(5), At(5, 9), createdAt: At(5, 9, 1));

        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(700m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task Only_completed_records_count()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100m, On(2), At(2, 9));
        BalanceSeed.Spend(db, wallet, 1m, On(3), At(3, 9), TransactionStatus.Captured);
        BalanceSeed.Spend(db, wallet, 2m, On(3), At(3, 10), TransactionStatus.Failed);
        BalanceSeed.Spend(db, wallet, 4m, On(3), At(3, 11), TransactionStatus.Cancelled);
        BalanceSeed.Earn(db, wallet, 8m, On(3), At(3, 12), TransactionStatus.Cancelled);
        BalanceSeed.State(db, wallet, 999m, On(4), At(4, 9), TransactionStatus.Captured);
        BalanceSeed.State(db, wallet, 555m, On(4), At(4, 10), TransactionStatus.Failed);

        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(-100m, CurrencyCode.Rsd));
        (await LastCheckedOnAsync(db, wallet)).Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_statement_stops_anchoring_and_the_one_before_it_takes_over()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100m, On(2), At(2, 9));
        var first = BalanceSeed.State(db, wallet, 1000m, On(3), At(3, 9));
        var second = BalanceSeed.State(db, wallet, 2000m, On(5), At(5, 9));
        BalanceSeed.Spend(db, wallet, 50m, On(6), At(6, 9));

        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(1950m, CurrencyCode.Rsd));
        (await LastCheckedOnAsync(db, wallet)).Should().Be(On(5));

        second.Status = TransactionStatus.Cancelled;
        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(950m, CurrencyCode.Rsd));
        (await LastCheckedOnAsync(db, wallet)).Should().Be(On(3));

        first.Status = TransactionStatus.Cancelled;
        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(-150m, CurrencyCode.Rsd));
        (await LastCheckedOnAsync(db, wallet)).Should().BeNull();
    }

    [Fact]
    public async Task Each_currency_in_a_wallet_is_its_own_line_the_wallet_currency_first()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 1200m, On(2), At(2, 9));
        BalanceSeed.Spend(db, wallet, 5000m, On(2), At(2, 10), currency: CurrencyCode.Kzt);
        BalanceSeed.Spend(db, wallet, 10m, On(2), At(2, 11), currency: CurrencyCode.Eur);
        // A statement in EUR re-anchors the EUR line only.
        BalanceSeed.State(db, wallet, 100m, On(3), At(3, 9), currency: CurrencyCode.Eur);
        BalanceSeed.Spend(db, wallet, 2.50m, On(4), At(4, 9), currency: CurrencyCode.Eur);

        (await SaveAndReadAsync(db, wallet)).Should().Equal(
            new Money(-1200m, CurrencyCode.Rsd),
            new Money(97.50m, CurrencyCode.Eur),
            new Money(-5000m, CurrencyCode.Kzt));
    }

    [Fact]
    public async Task Every_wallet_is_listed_archived_ones_marked_and_last()
    {
        await using var db = await MigratedAsync();
        var alpha = BalanceSeed.AddWallet(db, CurrencyCode.Rsd, "Alpha");
        var beta = BalanceSeed.AddWallet(db, CurrencyCode.Eur, "Beta", archived: true);
        var gamma = BalanceSeed.AddWallet(db, CurrencyCode.Usd, "gamma");
        BalanceSeed.Spend(db, alpha, 10m, On(2), At(2, 9));
        BalanceSeed.State(db, beta, 40m, On(3), At(3, 9));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var balances = await new EfBalanceReadModel(db).BalancesAsync(TestContext.Current.CancellationToken);

        // "Main Wallet" is the wallet AddCaptureModel seeds into every migrated database.
        balances.Select(balance => balance.WalletName).Should().Equal("Alpha", "gamma", "Main Wallet", "Beta");

        var alphaLine = balances.Single(balance => balance.WalletId == alpha.Id);
        alphaLine.Archived.Should().BeFalse();
        alphaLine.WalletCurrency.Should().Be(CurrencyCode.Rsd);
        alphaLine.Balances.Should().Equal(new Money(-10m, CurrencyCode.Rsd));
        alphaLine.LastCheckedOn.Should().BeNull();

        var betaLine = balances.Single(balance => balance.WalletId == beta.Id);
        betaLine.Archived.Should().BeTrue();
        betaLine.WalletCurrency.Should().Be(CurrencyCode.Eur);
        betaLine.Balances.Should().Equal(new Money(40m, CurrencyCode.Eur));
        betaLine.LastCheckedOn.Should().Be(On(3));

        var gammaLine = balances.Single(balance => balance.WalletId == gamma.Id);
        gammaLine.Balances.Should().BeEmpty("nothing has counted for this wallet yet");
        gammaLine.LastCheckedOn.Should().BeNull();
    }

    [Fact]
    public async Task The_balance_of_an_unknown_wallet_is_empty()
    {
        await using var db = await MigratedAsync();

        var balance = await new EfBalanceReadModel(db).BalanceOfAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        balance.Should().BeEmpty();
    }
}
