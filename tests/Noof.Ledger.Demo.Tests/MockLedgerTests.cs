using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Demo.Tests;

[Trait("Category", "Database")]
public sealed class MockLedgerTests(DemoTestDatabase database) : IClassFixture<DemoTestDatabase>
{
    [Fact]
    public async Task Every_wallet_ends_the_mock_month_on_its_hand_computed_balance()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var balances = await scope.ServiceProvider.GetRequiredService<IBalanceReadModel>()
            .BalancesAsync(TestContext.Current.CancellationToken);

        balances.ToDictionary(wallet => wallet.WalletName, wallet => wallet.Balances).Should().BeEquivalentTo(
            new Dictionary<string, IReadOnlyList<Money>>
            {
                // 3000 - 34.50 - 3.20 + 2800 - 42.00 = 5720.30, re-anchored to 5700.00 on 15.09, then - 40.30
                ["Wise"] = [new(5659.70m, CurrencyCode.Eur)],
                // 180000 - 3450 - 850 - 1200
                ["Raiffeisen"] = [new(174500.00m, CurrencyCode.Rsd)],
                // 600 - 4.50 - 45 + 450
                ["Cash"] = [new(1000.50m, CurrencyCode.Usd)],
                // 50000 - 599 - 1450
                ["Tinkoff"] = [new(47951.00m, CurrencyCode.Rub)],
                // 200000 - 6000 - 8500
                ["Kaspi"] = [new(185500.00m, CurrencyCode.Kzt)],
                ["Old Revolut"] = [new(900.00m, CurrencyCode.Eur)],
                ["Main Wallet"] = [],
            });
    }

    [Fact]
    public async Task No_two_transactions_share_a_moment_so_every_list_orders_them_the_same_way_each_run()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) - count(DISTINCT occurred_at) FROM transactions", connection);

        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).Should().Be(0L);
    }

    [Fact]
    public async Task Raiffeisen_takes_the_rsd_default_and_old_revolut_is_archived()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var wallets = await scope.ServiceProvider.GetRequiredService<IWalletAdmin>()
            .ListAsync(TestContext.Current.CancellationToken);

        wallets.Single(wallet => wallet.Name == "Raiffeisen").IsDefaultForCurrency.Should().BeTrue();
        wallets.Single(wallet => wallet.Name == "Main Wallet").IsDefaultForCurrency.Should().BeFalse();
        wallets.Single(wallet => wallet.Name == "Old Revolut").Archived.Should().BeTrue();
    }
}
