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
