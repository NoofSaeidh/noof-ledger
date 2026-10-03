using System.Diagnostics;
using AwesomeAssertions;
using Noof.Ledger.Host.Cli;

namespace Noof.Ledger.Host.Tests;

public class BugsVerbDispatchTests
{
    // Program.cs itself, not only TryParse: a bugs argument list the verb rejects ends the process with the usage line
    // before anything builds the web host. Were the dispatch missing, the web host this starts would reach only a dead
    // port, a temporary log directory and a temporary key ring, and is killed when the wait runs out.
    [Fact]
    public async Task A_usage_error_exits_2_with_the_usage_line_and_never_starts_the_web_host()
    {
        string? logDirectory = null;
        var keyRingDirectory = Directory.CreateTempSubdirectory("noof-host-test-dpkeys-").FullName;
        try
        {
            using var host = StartHost(["bugs", "list"], keyRingDirectory, out logDirectory);
            var output = host.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);

            var exited = await WaitForExitAsync(host, TimeSpan.FromSeconds(30));

            exited.Should().BeTrue("a usage error must end the process, not start the web host");
            host.ExitCode.Should().Be(2);
            (await output).Should().Be(BugsCommand.Usage + Environment.NewLine);
        }
        finally
        {
            await TestHostLogging.DeleteBestEffortAsync(logDirectory);
            await TestHostLogging.DeleteBestEffortAsync(keyRingDirectory);
        }
    }

    static Process StartHost(string[] args, string keyRingDirectory, out string logDirectory)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(HostAssembly());
        foreach (var argument in args)
            start.ArgumentList.Add(argument);

        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["Database__MigrateOnStartup"] = "false";
        start.Environment["ConnectionStrings__Ledger"] =
            "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2";
        start.Environment["DataProtection__KeyRingDirectory"] = keyRingDirectory;
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
