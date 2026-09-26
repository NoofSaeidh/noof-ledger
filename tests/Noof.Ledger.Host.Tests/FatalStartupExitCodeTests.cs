using System.Diagnostics;
using AwesomeAssertions;

namespace Noof.Ledger.Host.Tests;

public class FatalStartupExitCodeTests
{
    // Log.Fatal alone does not make the process exit non-zero -- Environment.ExitCode has to be
    // set explicitly in the catch, or a service manager watching the exit code sees a clean run.
    [Fact]
    public async Task A_fatal_startup_exception_yields_a_non_zero_exit_code()
    {
        string? logDirectory = null;
        try
        {
            using var host = StartHostWithBrokenTimeZone(out logDirectory);

            var exited = await WaitForExitAsync(host, TimeSpan.FromSeconds(30));

            exited.Should().BeTrue("a startup exception must not leave the process hanging");
            host.ExitCode.Should().NotBe(0, "a fatal startup failure must be detectable by a service manager");
        }
        finally
        {
            if (logDirectory is not null && Directory.Exists(logDirectory))
                Directory.Delete(logDirectory, recursive: true);
        }
    }

    static Process StartHostWithBrokenTimeZone(out string logDirectory)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { HostAssembly(), "--urls", "http://127.0.0.1:0" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.Environment["Database__MigrateOnStartup"] = "false";
        start.Environment["Capture__TimeZone"] = "Not/AZone";
        logDirectory = start.UseTempLogDirectory();

        return Process.Start(start)!;
    }

    static string HostAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            directory = directory.Parent;

        if (directory is null)
            throw new InvalidOperationException("Could not locate the repository root from the test output directory.");

        var assembly = Path.Combine(
            directory.FullName, "artifacts", "bin", "Noof.Ledger.Host", "debug", "Noof.Ledger.Host.dll");

        if (!File.Exists(assembly))
            throw new InvalidOperationException($"Host assembly not found at {assembly}.");

        return assembly;
    }

    static async Task<bool> WaitForExitAsync(Process host, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);

        try
        {
            await host.WaitForExitAsync(deadline.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
                await host.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
