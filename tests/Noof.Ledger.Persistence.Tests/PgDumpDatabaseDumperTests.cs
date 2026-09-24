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
        var dumper = new PgDumpDatabaseDumper(clone.ConnectionString, PgDumpPath);

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

        // Bounds libpq's own connection attempt: this sandbox's DNS resolver does not return
        // NXDOMAIN for an unreachable ".invalid" host, so an unbounded pg_dump hangs indefinitely
        // instead of failing fast. PgDumpDatabaseDumper.DumpAsync copies the test process's own
        // environment into the child process, so setting it here reaches pg_dump without changing
        // production code, which sets no connect timeout of its own.
        Environment.SetEnvironmentVariable("PGCONNECT_TIMEOUT", "3");
        try
        {
            var result = await dumper.DumpAsync(tempDump, TestContext.Current.CancellationToken);

            result.Succeeded.Should().BeFalse();
            result.Error.Should().NotBeNull();
            result.Error.Should().NotContain("marker-password-never-appears");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGCONNECT_TIMEOUT", null);
        }
    }
}
