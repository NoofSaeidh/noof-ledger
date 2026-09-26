using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Noof.Ledger.Host.Diagnostics;
using Noof.Ledger.Host.Logging;

namespace Noof.Ledger.Host.Tests;

// PR #1 review item 5: Logging:File:FileSizeLimitBytes and Logging:File:RetainedFileCountLimit
// were hard-coded consts in LoggingSetup - now read from configuration the same way
// Logging:File:Directory already was, defaulting to the same 50 MB / 14 files. Proved through the
// real Serilog rolling-file sink (a source-text check could not tell a wired config value from a
// dead one), not just a resolver-returns-the-right-number unit test.
public class LoggingFileLimitsTests
{
    [Fact]
    public async Task A_small_configured_file_size_limit_forces_a_roll_and_a_small_retained_count_caps_the_files_kept()
    {
        var directory = Directory.CreateTempSubdirectory("noof-logging-file-limits-test-").FullName;

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:File:FileSizeLimitBytes"] = "200",
                    ["Logging:File:RetainedFileCountLimit"] = "2",
                })
                .Build();

            var redactor = new SecretRedactor(new BootstrapSecretSource(string.Empty));
            var logger = LoggingSetup.CreateBootstrapLogger(directory, redactor, configuration);

            for (var i = 0; i < 100; i++)
                logger.Information("Padding line {Line} to push the file past a 200-byte limit", i);

            (logger as IDisposable)?.Dispose();

            var files = await FilesWithRetryAsync(directory);

            files.Length.Should().BeGreaterThan(1,
                "a 200-byte FileSizeLimitBytes from configuration must force more than one file - the " +
                "default 50 MB limit never would have from this little data");
            files.Length.Should().BeLessThanOrEqualTo(2,
                "RetainedFileCountLimit=2 from configuration must cap how many files are kept");
        }
        finally
        {
            await DeleteWithRetryAsync(new DirectoryInfo(directory));
        }
    }

    static async Task<string[]> FilesWithRetryAsync(string directory)
    {
        for (var attempt = 1; ; attempt++)
        {
            var files = Directory.GetFiles(directory, "noof-ledger-*.log");
            if (files.Length > 1 || attempt >= 10)
                return files;

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    static async Task DeleteWithRetryAsync(DirectoryInfo directory)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                directory.Delete(recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
    }
}
