using System.Diagnostics;

namespace Noof.Ledger.Host.Tests;

internal static class BuiltHostProcess
{
    // Owns the temp log directory so that no spawned host can write into the operator's real one
    // (TestHostLogDirectoryTests).
    public static ProcessStartInfo StartInfo(IEnumerable<string> args, out string logDirectory)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(AssemblyPath());
        foreach (var argument in args)
            start.ArgumentList.Add(argument);

        logDirectory = start.UseTempLogDirectory();
        return start;
    }

    static string AssemblyPath()
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

    // Kills the host when it outlives the wait. Without this a failing test would leave a server running - for the
    // loopback guard, one bound to every interface, the exact state under test.
    public static async Task<bool> WaitForExitAsync(Process host, TimeSpan timeout)
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
                // CancellationToken.None deliberately: a cancelled test run must still wait for the killed host to
                // actually exit, or it leaks the process.
                await host.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
