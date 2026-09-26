using System.Globalization;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;
using Noof.Ledger.TestKit;
using Npgsql;

namespace Noof.Ledger.Host.Tests;

[Collection("culture")]
[Trait("Category", "Database")]
public sealed class DashboardCultureTests
{
    static readonly Guid EurWalletId = Guid.NewGuid();
    static readonly Guid RsdWalletId = Guid.NewGuid();
    const string TimeZone = "Europe/Belgrade";
    static readonly DateOnly Day1 = new(2026, 9, 1);
    static readonly DateTimeOffset At1 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public async Task The_served_dashboard_html_is_culture_independent(string cultureName)
    {
        if (!await DatabaseIsReachableAsync(TestContext.Current.CancellationToken))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_dashboard_culture_{Guid.NewGuid():N}";
        await CreateCloneAsync(databaseName, TestContext.Current.CancellationToken);

        var cloneBuilder = new NpgsqlConnectionStringBuilder(DatabaseSettings.AdminConnectionString) { Database = databaseName };

        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var culture = new CultureInfo(cultureName);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            await using (var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(cloneBuilder.ConnectionString).Options))
            {
                db.Wallets.AddRange(
                    new Wallet { Id = EurWalletId, Name = "Wise EUR", Currency = CurrencyCode.Eur, Aliases = [], IsDefaultForCurrency = false, Archived = false, CreatedAt = At1 },
                    new Wallet { Id = RsdWalletId, Name = "Raiffeisen RSD", Currency = CurrencyCode.Rsd, Aliases = [], IsDefaultForCurrency = false, Archived = false, CreatedAt = At1 });

                var eurTransactionId = Guid.NewGuid();
                db.Transactions.Add(new Transaction
                {
                    Id = eurTransactionId, WalletId = EurWalletId, Kind = TransactionKind.BalanceCheck, CaptureKind = CaptureKind.Manual,
                    RawText = "Opening balance", Status = TransactionStatus.Completed, TimeZoneId = TimeZone,
                    OccurredAt = At1, OccurredOn = Day1, TelegramChatId = null, TelegramMessageId = null, CreatedAt = At1,
                });
                db.BalanceChecks.Add(new BalanceCheck { TransactionId = eurTransactionId, WalletId = EurWalletId, Stated = new Money(1000.00m, CurrencyCode.Eur), ComputedBefore = 0m });

                var expenseTransactionId = Guid.NewGuid();
                db.Transactions.Add(new Transaction
                {
                    Id = expenseTransactionId, WalletId = EurWalletId, Kind = TransactionKind.Expense, CaptureKind = CaptureKind.Manual,
                    RawText = "seeded", Status = TransactionStatus.Completed, TimeZoneId = TimeZone,
                    OccurredAt = At1.AddDays(1), OccurredOn = Day1.AddDays(1), TelegramChatId = null, TelegramMessageId = null, CreatedAt = At1.AddDays(1),
                });
                db.Entries.Add(new Entry { Id = Guid.NewGuid(), TransactionId = expenseTransactionId, WalletId = EurWalletId, Amount = new Money(-0.30m, CurrencyCode.Eur), Role = EntryRole.Principal });

                var rsdTransactionId = Guid.NewGuid();
                db.Transactions.Add(new Transaction
                {
                    Id = rsdTransactionId, WalletId = RsdWalletId, Kind = TransactionKind.BalanceCheck, CaptureKind = CaptureKind.Manual,
                    RawText = "Opening balance", Status = TransactionStatus.Completed, TimeZoneId = TimeZone,
                    OccurredAt = At1, OccurredOn = Day1, TelegramChatId = null, TelegramMessageId = null, CreatedAt = At1,
                });
                db.BalanceChecks.Add(new BalanceCheck { TransactionId = rsdTransactionId, WalletId = RsdWalletId, Stated = new Money(44_800.00m, CurrencyCode.Rsd), ComputedBefore = 0m });

                await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseTempLogDirectory();
                builder.UseSetting("ConnectionStrings:Ledger", cloneBuilder.ConnectionString);
                builder.UseSetting("Database:MigrateOnStartup", "false");
                builder.UseSetting("Backup:Enabled", "false");
                builder.ConfigureServices(FakeUserStore.Register);
            });

            using var client = factory.CreateClient();

            var gate = factory.Services.GetRequiredService<IDatabaseGate>();
            await gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

            await LoginHelper.PostWithTokenAsync(client, "noof", "correct");
            var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

            html.Should().Contain("44,800.00 RSD");
            html.Should().Contain("999.70 EUR");
            html.Should().NotContain("44 800,00");
            html.Should().NotContain("999,70");
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = null;
            CultureInfo.DefaultThreadCurrentUICulture = null;
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;

            await DropCloneAsync(databaseName);
        }
    }

    static async Task<bool> DatabaseIsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
            await connection.OpenAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static async Task CreateCloneAsync(string name, CancellationToken cancellationToken)
    {
        await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
        await admin.OpenAsync(cancellationToken);
        await using var create = new NpgsqlCommand(
            $"CREATE DATABASE \"{name}\" TEMPLATE {DatabaseSettings.TemplateDatabase}", admin);
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    static async Task DropCloneAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
        await admin.OpenAsync(CancellationToken.None);
        // DROP DATABASE waits on a Postgres checkpoint before it can remove the files, which can
        // exceed Npgsql's default 30s command timeout under load - same reason PostgresFixture and
        // CookieModeHostFixture both raise it.
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin)
        {
            CommandTimeout = 120,
        };
        await drop.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
