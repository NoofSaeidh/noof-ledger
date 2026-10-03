using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Host.Cli;
using Noof.Ledger.Persistence;
using Noof.Ledger.TestKit;
using Npgsql;

namespace Noof.Ledger.Host.Tests;

// ExportAsync over a template clone, an empty database and a port nothing listens on. Never RunAsync with
// arguments: it resolves the operator's own connection string, which names noof_ledger.
[Trait("Category", "Database")]
public sealed class BugsCommandDbTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 5, 0, TimeSpan.Zero);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Writes_the_open_reports_to_a_file_named_by_the_local_minute_and_prints_its_path()
    {
        if (!await DatabaseIsReachableAsync(Ct))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_bugs_export_{Guid.NewGuid():N}";
        await DatabaseSettings.CreateDatabaseFromTemplateAsync(databaseName, Ct);
        var outputRoot = NewOutputRoot();
        try
        {
            var clock = BelgradeClock();
            await using var services = BuildServices(ConnectionStringFor(databaseName), clock);
            var number = await SaveReportAsync(services, "the dashboard total looks off");
            var outputDirectory = Path.Combine(outputRoot, "bug-reports");
            var output = new StringWriter();

            var exitCode = await BugsCommand.ExportAsync(
                services, new BugsExportArguments(All: false, outputDirectory), clock, output, Ct);

            // 14:05 UTC is 16:05 in Belgrade (CEST): the file is named by the local minute, the header by UTC.
            var expectedPath = Path.Combine(outputDirectory, "2026-10-02-1605.md");
            exitCode.Should().Be(0);
            output.ToString().Should().Be(expectedPath + Environment.NewLine);
            var bytes = await File.ReadAllBytesAsync(expectedPath, Ct);
            bytes[0].Should().Be((byte)'#', "the file is UTF-8 without a byte-order mark");
            var markdown = System.Text.Encoding.UTF8.GetString(bytes);
            markdown.Should().StartWith("# noof-ledger bug reports");
            markdown.Should().Contain("Exported 2026-10-02 14:05 UTC · 1 report");
            markdown.Should().Contain($"## Bug report #{number}");
            markdown.Should().Contain("the dashboard total looks off");
        }
        finally
        {
            await BestEffortDelete.DirectoryAsync(outputRoot);
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Leaves_closed_reports_out_and_writes_no_file_when_none_is_open()
    {
        if (!await DatabaseIsReachableAsync(Ct))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_bugs_export_{Guid.NewGuid():N}";
        await DatabaseSettings.CreateDatabaseFromTemplateAsync(databaseName, Ct);
        var outputRoot = NewOutputRoot();
        try
        {
            var clock = BelgradeClock();
            await using var services = BuildServices(ConnectionStringFor(databaseName), clock);
            await CloseAsync(services, await SaveReportAsync(services, "already sorted"));
            var output = new StringWriter();

            var exitCode = await BugsCommand.ExportAsync(
                services, new BugsExportArguments(All: false, outputRoot), clock, output, Ct);

            exitCode.Should().Be(0);
            output.ToString().Should().Be("No open bug reports." + Environment.NewLine);
            Directory.Exists(outputRoot).Should().BeFalse("nothing to export writes no file and creates no folder");
        }
        finally
        {
            await BestEffortDelete.DirectoryAsync(outputRoot);
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task With_all_exports_closed_reports_too()
    {
        if (!await DatabaseIsReachableAsync(Ct))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_bugs_export_{Guid.NewGuid():N}";
        await DatabaseSettings.CreateDatabaseFromTemplateAsync(databaseName, Ct);
        var outputRoot = NewOutputRoot();
        try
        {
            var clock = BelgradeClock();
            await using var services = BuildServices(ConnectionStringFor(databaseName), clock);
            var number = await SaveReportAsync(services, "already sorted");
            await CloseAsync(services, number);
            var output = new StringWriter();

            var exitCode = await BugsCommand.ExportAsync(
                services, new BugsExportArguments(All: true, outputRoot), clock, output, Ct);

            exitCode.Should().Be(0);
            var markdown = await File.ReadAllTextAsync(Path.Combine(outputRoot, "2026-10-02-1605.md"), Ct);
            markdown.Should().Contain($"## Bug report #{number}");
            markdown.Should().Contain("- Status: Closed (closed ");
        }
        finally
        {
            await BestEffortDelete.DirectoryAsync(outputRoot);
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task With_all_and_no_report_at_all_says_so()
    {
        if (!await DatabaseIsReachableAsync(Ct))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_bugs_export_{Guid.NewGuid():N}";
        await DatabaseSettings.CreateDatabaseFromTemplateAsync(databaseName, Ct);
        var outputRoot = NewOutputRoot();
        try
        {
            var clock = BelgradeClock();
            await using var services = BuildServices(ConnectionStringFor(databaseName), clock);
            var output = new StringWriter();

            var exitCode = await BugsCommand.ExportAsync(
                services, new BugsExportArguments(All: true, outputRoot), clock, output, Ct);

            exitCode.Should().Be(0);
            output.ToString().Should().Be("No bug reports." + Environment.NewLine);
            Directory.Exists(outputRoot).Should().BeFalse();
        }
        finally
        {
            await BestEffortDelete.DirectoryAsync(outputRoot);
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task A_database_that_cannot_be_reached_ends_with_one_line_and_exit_code_1()
    {
        var clock = BelgradeClock();
        await using var services = BuildServices(
            "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=5", clock);
        var output = new StringWriter();

        var exitCode = await BugsCommand.ExportAsync(
            services, new BugsExportArguments(All: false, NewOutputRoot()), clock, output, Ct);

        exitCode.Should().Be(1);
        output.ToString().Should().MatchRegex(@"^Cannot reach PostgreSQL \(\w+\) - start it and try again\.\r?\n$");
    }

    [Fact]
    public async Task An_unmigrated_database_ends_with_one_line_and_exit_code_1()
    {
        if (!await DatabaseIsReachableAsync(Ct))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_bugs_export_{Guid.NewGuid():N}";
        await DatabaseSettings.CreateEmptyDatabaseAsync(databaseName, Ct);
        var outputRoot = NewOutputRoot();
        try
        {
            var clock = BelgradeClock();
            await using var services = BuildServices(ConnectionStringFor(databaseName), clock);
            var output = new StringWriter();

            var exitCode = await BugsCommand.ExportAsync(
                services, new BugsExportArguments(All: false, outputRoot), clock, output, Ct);

            exitCode.Should().Be(1);
            output.ToString().Should().MatchRegex(
                @"^The database is not migrated \(\d+ pending\) - start the app once to migrate it\.\r?\n$");
            Directory.Exists(outputRoot).Should().BeFalse();
        }
        finally
        {
            await BestEffortDelete.DirectoryAsync(outputRoot);
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task A_failure_while_writing_ends_with_one_line_and_exit_code_1()
    {
        if (!await DatabaseIsReachableAsync(Ct))
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var databaseName = $"noof_bugs_export_{Guid.NewGuid():N}";
        await DatabaseSettings.CreateDatabaseFromTemplateAsync(databaseName, Ct);
        var outputRoot = NewOutputRoot();
        try
        {
            var clock = BelgradeClock();
            await using var services = BuildServices(ConnectionStringFor(databaseName), clock);
            await SaveReportAsync(services, "the dashboard total looks off");
            Directory.CreateDirectory(outputRoot);
            var aFileWhereTheFolderShouldBe = Path.Combine(outputRoot, "bug-reports");
            await File.WriteAllTextAsync(aFileWhereTheFolderShouldBe, "not a folder", Ct);
            var output = new StringWriter();

            var exitCode = await BugsCommand.ExportAsync(
                services, new BugsExportArguments(All: false, aFileWhereTheFolderShouldBe), clock, output, Ct);

            exitCode.Should().Be(1);
            output.ToString().Should().MatchRegex(@"^Export failed \(\w+\)\.\r?\n$");
        }
        finally
        {
            await BestEffortDelete.DirectoryAsync(outputRoot);
            await DropDatabaseAsync(databaseName);
        }
    }

    // Always an explicit connection string: an empty ConnectionStrings:Ledger falls back to the operator's
    // db.connection, which names noof_ledger.
    static ServiceProvider BuildServices(string connectionString, TimeProvider clock)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);
        services.AddNoofApplication(
            new SlowOperationOptions(),
            new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });
        services.AddNoofPersistence(configuration, maxJobAttempts: 1);
        return services.BuildServiceProvider();
    }

    static FakeTimeProvider BelgradeClock()
    {
        var clock = new FakeTimeProvider(Now);
        clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade"));
        return clock;
    }

    static async Task<int> SaveReportAsync(IServiceProvider services, string text)
    {
        await using var scope = services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<IBugReportStore>()
            .SaveFromTelegramAsync(new TelegramBugReport(ChatId: 1001, MessageId: 1, text, TransactionId: null), Ct);
        return saved.Number;
    }

    static async Task CloseAsync(IServiceProvider services, int number)
    {
        await using var scope = services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IBugReportStore>()
            .SetStatusAsync(number, BugReportStatus.Closed, Ct)).Should().BeTrue();
    }

    static string NewOutputRoot() => Path.Combine(Path.GetTempPath(), $"noof-bugs-export-{Guid.NewGuid():N}");

    static string ConnectionStringFor(string databaseName) =>
        new NpgsqlConnectionStringBuilder(DatabaseSettings.AdminConnectionString) { Database = databaseName }.ConnectionString;

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

    static Task DropDatabaseAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();
        return DatabaseSettings.DropDatabaseAsync(name, CancellationToken.None);
    }
}
