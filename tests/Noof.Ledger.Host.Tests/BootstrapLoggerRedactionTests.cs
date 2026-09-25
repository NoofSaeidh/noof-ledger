using AwesomeAssertions;
using Noof.Ledger.Host.Diagnostics;
using Noof.Ledger.Host.Logging;

namespace Noof.Ledger.Host.Tests;

// PR #1 review item 2: the bootstrap logger (live before UseSerilog reconfigures logging, and
// therefore the one thing a startup exception is ever reported through) used to write straight to
// console+file with no SecretRedactor - a connection failure whose exception message named the
// database password would have printed it in the clear. CreateBootstrapLogger now takes the same
// SecretRedactor the fully configured logger uses, seeded (by Program.cs) with the one secret
// knowable this early: the database password from the connection string.
public class BootstrapLoggerRedactionTests
{
    [Fact]
    public async Task An_event_with_the_bootstrap_password_is_written_redacted()
    {
        const string password = "s3cr3t-bootstrap-password";
        var directory = Directory.CreateTempSubdirectory("noof-bootstrap-redaction-test-").FullName;

        try
        {
            var redactor = new SecretRedactor(new BootstrapSecretSource(password));
            var logger = LoggingSetup.CreateBootstrapLogger(directory, redactor);

            logger.Fatal("Startup failed for connection {ConnectionString}", $"Password={password}");
            (logger as IDisposable)?.Dispose();

            var logFile = Directory.EnumerateFiles(directory, "noof-ledger-*.log").Single();
            var contents = await ReadAllTextWithRetryAsync(logFile);

            contents.Should().Contain("***").And.NotContain(password);
        }
        finally
        {
            await DeleteWithRetryAsync(new DirectoryInfo(directory));
        }
    }

    static async Task<string> ReadAllTextWithRetryAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
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
