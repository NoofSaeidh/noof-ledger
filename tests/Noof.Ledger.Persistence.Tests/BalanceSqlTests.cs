using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Balances;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class BalanceSqlTests(PostgresFixture fixture)
{
    static DateOnly On(int day) => new(2026, 9, day);

    static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 9, day, hour, minute, 0, TimeSpan.Zero);

    async Task<LedgerDbContext> MigratedAsync()
    {
        var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return db;
    }

    static Task<decimal> AsOfAsync(LedgerDbContext db, Wallet wallet, Transaction cutOff, Guid? excluding = null) =>
        BalanceSql.AsOfAsync(
            db, wallet.Id, wallet.Currency, cutOff.OccurredOn, cutOff.OccurredAt, excluding ?? cutOff.Id,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task It_leaves_out_the_excluded_record_and_everything_after_the_cut_off()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100m, On(2), At(2, 9));
        BalanceSeed.State(db, wallet, 1000m, On(3), At(3, 9));
        var applying = BalanceSeed.Spend(db, wallet, 50m, On(4), At(4, 10));
        var later = BalanceSeed.Spend(db, wallet, 30m, On(6), At(6, 9));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await AsOfAsync(db, wallet, applying)).Should().Be(1000m);
        (await AsOfAsync(db, wallet, applying, excluding: later.Id)).Should().Be(
            950m, "a record exactly at the cut-off counts unless it is the one excluded");

        var sameInstantInBelgrade = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.FromHours(2));
        (await BalanceSql.AsOfAsync(
            db, wallet.Id, wallet.Currency, On(4), sameInstantInBelgrade, applying.Id, TestContext.Current.CancellationToken))
            .Should().Be(1000m, "a cut-off written with a non-zero offset is the same instant and gives the same answer");
    }

    [Fact]
    public async Task A_statement_being_applied_is_not_its_own_anchor()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100m, On(2), At(2, 9));
        BalanceSeed.State(db, wallet, 1000m, On(3), At(3, 9));
        BalanceSeed.Spend(db, wallet, 50m, On(4), At(4, 9));
        var statement = BalanceSeed.State(db, wallet, 2000m, On(5), At(5, 9));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await AsOfAsync(db, wallet, statement)).Should().Be(950m);
        (await AsOfAsync(db, wallet, statement, excluding: Guid.NewGuid())).Should().Be(
            2000m, "without the exclusion the statement anchors itself, which is what the exclusion is for");
    }

    [Fact]
    public async Task Before_any_statement_it_is_the_sum_of_the_entries()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100m, On(2), At(2, 9));
        var income = BalanceSeed.Earn(db, wallet, 40m, On(3), At(3, 9));
        BalanceSeed.State(db, wallet, 1000m, On(5), At(5, 9));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await AsOfAsync(db, wallet, income, excluding: Guid.NewGuid())).Should().Be(-60m);
    }

    [Fact]
    public async Task Only_completed_records_count()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100m, On(2), At(2, 9));
        BalanceSeed.Spend(db, wallet, 7m, On(2), At(2, 10), TransactionStatus.Cancelled);
        BalanceSeed.State(db, wallet, 5000m, On(2), At(2, 11), TransactionStatus.Cancelled);
        var cutOff = BalanceSeed.Spend(db, wallet, 3m, On(3), At(3, 9), TransactionStatus.Captured);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await AsOfAsync(db, wallet, cutOff, excluding: Guid.NewGuid())).Should().Be(-100m);
    }

    [Fact]
    public async Task A_wallet_with_nothing_counted_holds_zero()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Eur);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var balance = await BalanceSql.AsOfAsync(
            db, wallet.Id, CurrencyCode.Eur, On(10), At(10, 9), Guid.NewGuid(), TestContext.Current.CancellationToken);

        balance.Should().Be(0m);
    }

    [Fact]
    public async Task At_the_end_of_the_ledger_it_agrees_with_the_view()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 1234.56m, On(2), At(2, 9));
        BalanceSeed.State(db, wallet, 45_000m, On(3), At(3, 9));
        BalanceSeed.Spend(db, wallet, 0.44m, On(3), At(3, 9, 30));
        BalanceSeed.Earn(db, wallet, 2000m, On(4), At(4, 9));
        BalanceSeed.Spend(db, wallet, 999m, On(4), At(4, 10), TransactionStatus.Cancelled);
        BalanceSeed.Spend(db, wallet, 12.30m, On(5), At(5, 9), currency: CurrencyCode.Eur);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var endOfLedger = new DateOnly(2100, 1, 1);
        var endInstant = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var view = await new EfBalanceReadModel(db).BalanceOfAsync(wallet.Id, TestContext.Current.CancellationToken);
        var rsd = await BalanceSql.AsOfAsync(
            db, wallet.Id, CurrencyCode.Rsd, endOfLedger, endInstant, Guid.NewGuid(), TestContext.Current.CancellationToken);
        var eur = await BalanceSql.AsOfAsync(
            db, wallet.Id, CurrencyCode.Eur, endOfLedger, endInstant, Guid.NewGuid(), TestContext.Current.CancellationToken);

        view.Should().Equal(new Money(46_999.56m, CurrencyCode.Rsd), new Money(-12.30m, CurrencyCode.Eur));
        rsd.Should().Be(46_999.56m);
        eur.Should().Be(-12.30m);
    }
}
