using System.Diagnostics;
using AwesomeAssertions;

namespace Noof.Ledger.Host.Tests;

public class LoopbackGuardTerminatesTests
{
    // The guard runs in an ApplicationStarted callback, and throwing from there does NOT stop the
    // host — the hosting layer catches it, logs it, and Kestrel keeps serving. Every other test of
    // the guard exercises the pure function, so nothing but this proves the process actually dies.
    [Fact]
    public async Task An_exposed_binding_terminates_the_host()
    {
        using var host = StartHost("http://0.0.0.0:0");

        var exited = await WaitForExitAsync(host, TimeSpan.FromSeconds(30));

        exited.Should().BeTrue("the host must refuse to serve, not merely log and carry on");
        host.ExitCode.Should().Be(1, "a safety refusal must be detectable by a service manager");
    }

    static Process StartHost(string urls)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { HostAssembly(), "--urls", urls },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.Environment["Database__MigrateOnStartup"] = "false";
        start.Environment["ConnectionStrings__Ledger"] =
            "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2";

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

    // Kills the host when it outlives the wait. Without this a FAILING first test would leave a
    // server bound to every interface — the exact state under test.
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
                // CancellationToken.None deliberately: a cancelled test run must still wait for the
                // killed host to actually exit, or it leaks a process bound to every interface.
                await host.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
