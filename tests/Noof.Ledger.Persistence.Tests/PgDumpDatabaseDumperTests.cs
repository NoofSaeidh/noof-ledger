using System.Diagnostics;
using AwesomeAssertions;
using Noof.Ledger.Persistence.Backup;
using Noof.Ledger.TestKit;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class PgDumpDatabaseDumperTests(PostgresFixture fixture) : IAsyncLifetime
{
    string? tempDump;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (tempDump is not null && File.Exists(tempDump))
            File.Delete(tempDump);
        return ValueTask.CompletedTask;
    }

    static string PgDumpPath => Environment.GetEnvironmentVariable("NOOF_TEST_PGDUMP") ?? PgDumpDatabaseDumper.DefaultPath;

    [Fact]
    public async Task Dumps_a_template_clone_to_a_non_empty_file_pg_restore_accepts()
    {
        if (!File.Exists(PgDumpPath))
            Assert.Skip($"pg_dump.exe not found at {PgDumpPath} - install PostgreSQL 18 or set Backup:PgDumpPath (test override: NOOF_TEST_PGDUMP).");

        await using var clone = await fixture.CreateDatabaseAsync();
        tempDump = Path.Combine(Path.GetTempPath(), $"noof-backup-test-{Guid.NewGuid():N}.dump");
        var dumper = new PgDumpDatabaseDumper(DatabaseSettings.For(clone.Database), PgDumpPath);

        var result = await dumper.DumpAsync(tempDump, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Error);
        File.Exists(tempDump).Should().BeTrue();
        new FileInfo(tempDump).Length.Should().BeGreaterThan(0);

        var pgRestore = PgDumpPath.Replace("pg_dump.exe", "pg_restore.exe", StringComparison.Ordinal);
        var list = Process.Start(new ProcessStartInfo(pgRestore, ["--list", tempDump])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = await list.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await list.WaitForExitAsync(TestContext.Current.CancellationToken);

        list.ExitCode.Should().Be(0, output);
        output.Should().Contain("wallets");
    }

    [Fact]
    public async Task A_bad_host_fails_without_the_password_anywhere_in_the_error()
    {
        if (!File.Exists(PgDumpPath))
            Assert.Skip($"pg_dump.exe not found at {PgDumpPath} - install PostgreSQL 18 or set Backup:PgDumpPath (test override: NOOF_TEST_PGDUMP).");

        var admin = new NpgsqlConnectionStringBuilder(DatabaseSettings.AdminConnectionString)
        {
            Host = "noof-ledger-unreachable-host.invalid",
            Password = "marker-password-never-appears",
        };
        tempDump = Path.Combine(Path.GetTempPath(), $"noof-backup-test-{Guid.NewGuid():N}.dump");
        var dumper = new PgDumpDatabaseDumper(admin.ConnectionString, PgDumpPath);

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await dumper.DumpAsync(tempDump, bound.Token);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        result.Error.Should().NotContain("marker-password-never-appears");
    }

    [Fact]
    public async Task Cancelling_mid_dump_kills_the_pg_dump_process_instead_of_leaving_it_running()
    {
        // M-4 (Phase 4 final review): WaitForExitAsync(cancellationToken) used to return on cancel
        // while pg_dump kept running and kept writing the .tmp file. Checking "is the process gone
        // shortly after cancellation" only proves something if pg_dump would otherwise still be
        // running at that point - so this first times an uncancelled dump of the same table, then
        // cancels a second one at a quarter of that time and requires the process to be gone within
        // another quarter, well before it could ever finish on its own.
        if (!File.Exists(PgDumpPath))
            Assert.Skip($"pg_dump.exe not found at {PgDumpPath} - install PostgreSQL 18 or set Backup:PgDumpPath (test override: NOOF_TEST_PGDUMP).");

        await using var clone = await fixture.CreateDatabaseAsync();
        await using (var connection = new NpgsqlConnection(DatabaseSettings.For(clone.Database)))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var seed = new NpgsqlCommand(
                "CREATE TABLE bulk_filler AS SELECT g, md5(g::text) AS payload FROM generate_series(1, 5000000) g;",
                connection)
            { CommandTimeout = 180 };
            await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var dumper = new PgDumpDatabaseDumper(DatabaseSettings.For(clone.Database), PgDumpPath);

        var calibrationPath = Path.Combine(Path.GetTempPath(), $"noof-backup-test-{Guid.NewGuid():N}-calibration.dump");
        var stopwatch = Stopwatch.StartNew();
        var calibration = await dumper.DumpAsync(calibrationPath, TestContext.Current.CancellationToken);
        stopwatch.Stop();
        File.Delete(calibrationPath);
        calibration.Succeeded.Should().BeTrue(calibration.Error);
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1),
            "the test needs a dump slow enough to still be running a quarter of the way through - raise the row count if this machine dumped it faster than that");

        tempDump = Path.Combine(Path.GetTempPath(), $"noof-backup-test-{Guid.NewGuid():N}.dump");
        var before = Process.GetProcessesByName("pg_dump").Length;
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(stopwatch.Elapsed / 4);
        var act = async () => await dumper.DumpAsync(tempDump, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        var deadline = DateTime.UtcNow + stopwatch.Elapsed / 4;
        while (Process.GetProcessesByName("pg_dump").Length > before && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);

        Process.GetProcessesByName("pg_dump").Length.Should().Be(
            before, "the cancelled dump's pg_dump process must be killed, not left running");
    }

    [Fact]
    public async Task A_clone_with_no_password_fails_quickly_instead_of_hanging_on_a_console_prompt()
    {
        if (!File.Exists(PgDumpPath))
            Assert.Skip($"pg_dump.exe not found at {PgDumpPath} - install PostgreSQL 18 or set Backup:PgDumpPath (test override: NOOF_TEST_PGDUMP).");

        await using var clone = await fixture.CreateDatabaseAsync();
        var noPassword = new NpgsqlConnectionStringBuilder(DatabaseSettings.For(clone.Database))
        {
            Password = null,
        };
        tempDump = Path.Combine(Path.GetTempPath(), $"noof-backup-test-{Guid.NewGuid():N}.dump");
        var dumper = new PgDumpDatabaseDumper(noPassword.ConnectionString, PgDumpPath);

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await dumper.DumpAsync(tempDump, bound.Token);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }
}
