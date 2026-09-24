using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Backup;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class RestoreRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    string? tempDump;

    static string PgDumpPath => Environment.GetEnvironmentVariable("NOOF_TEST_PGDUMP") ?? PgDumpDatabaseDumper.DefaultPath;
    static string PgRestorePath => PgDumpPath.Replace("pg_dump.exe", "pg_restore.exe", StringComparison.Ordinal);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (tempDump is not null && File.Exists(tempDump))
            File.Delete(tempDump);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_restored_dump_has_the_same_balances_and_row_counts_as_the_source()
    {
        if (!File.Exists(PgDumpPath))
            Assert.Skip($"pg_dump.exe not found at {PgDumpPath} - install PostgreSQL 18 or set Backup:PgDumpPath (test override: NOOF_TEST_PGDUMP).");

        var sourceConnectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();
        await using (var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(sourceConnectionString).Options))
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var eurWallet = BalanceSeed.AddWallet(db, CurrencyCode.Eur, "Wise EUR");
            var rsdWallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd, "Raiffeisen RSD");
            BalanceSeed.State(db, eurWallet, 1000.00m, new DateOnly(2026, 9, 1), new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
            BalanceSeed.Spend(db, eurWallet, 0.30m, new DateOnly(2026, 9, 2), new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero));
            BalanceSeed.State(db, rsdWallet, 44_800.00m, new DateOnly(2026, 9, 1), new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
            BalanceSeed.Earn(db, rsdWallet, 200.00m, new DateOnly(2026, 9, 2), new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        tempDump = Path.Combine(Path.GetTempPath(), $"noof-restore-roundtrip-{Guid.NewGuid():N}.dump");
        var dumper = new PgDumpDatabaseDumper(sourceConnectionString, PgDumpPath);
        var dumpResult = await dumper.DumpAsync(tempDump, TestContext.Current.CancellationToken);
        dumpResult.Succeeded.Should().BeTrue(dumpResult.Error);

        var restoredConnectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();
        var restoredBuilder = new Npgsql.NpgsqlConnectionStringBuilder(restoredConnectionString);
        var restore = Process.Start(new ProcessStartInfo(PgRestorePath,
            ["--no-owner", "--no-privileges", "-h", restoredBuilder.Host!, "-p", restoredBuilder.Port.ToString(), "-U", restoredBuilder.Username!, "-d", restoredBuilder.Database!, tempDump])
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            Environment = { ["PGPASSWORD"] = restoredBuilder.Password ?? string.Empty },
        })!;
        var restoreError = await restore.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await restore.WaitForExitAsync(TestContext.Current.CancellationToken);
        restore.ExitCode.Should().Be(0, restoreError);

        await using var source = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(sourceConnectionString).Options);
        await using var restored = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(restoredConnectionString).Options);

        var sourceBalances = await source.Database.SqlQuery<string>(
            $"SELECT wallet_id::text || ':' || currency || ':' || balance::text FROM wallet_balances ORDER BY wallet_id, currency")
            .ToListAsync(TestContext.Current.CancellationToken);
        var restoredBalances = await restored.Database.SqlQuery<string>(
            $"SELECT wallet_id::text || ':' || currency || ':' || balance::text FROM wallet_balances ORDER BY wallet_id, currency")
            .ToListAsync(TestContext.Current.CancellationToken);
        restoredBalances.Should().BeEquivalentTo(sourceBalances);

        (await restored.Wallets.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(await source.Wallets.CountAsync(TestContext.Current.CancellationToken));
        (await restored.Transactions.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(await source.Transactions.CountAsync(TestContext.Current.CancellationToken));
        (await restored.Entries.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(await source.Entries.CountAsync(TestContext.Current.CancellationToken));
        (await restored.BalanceChecks.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(await source.BalanceChecks.CountAsync(TestContext.Current.CancellationToken));
    }
}
