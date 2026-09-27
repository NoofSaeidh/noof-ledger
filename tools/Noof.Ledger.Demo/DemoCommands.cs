using System.Diagnostics;

namespace Noof.Ledger.Demo;

internal static class DemoCommands
{
    public static async Task<int> RefreshAsync()
    {
        var paths = DemoPaths.ForOperator();
        using var exclusive = DemoLock.Acquire(paths);
        await RefreshDemoAsync(paths, CancellationToken.None);
        Console.WriteLine($"{DemoDatabase.Name} refreshed with the mock data.");
        return 0;
    }

    public static async Task<int> StartAsync()
    {
        var paths = DemoPaths.ForOperator();
        using var exclusive = DemoLock.Acquire(paths);
        var connectionString = await RefreshDemoAsync(paths, CancellationToken.None);

        using var process = Process.Start(DemoHost.StartInfo(connectionString, paths, redirect: false))
            ?? throw new InvalidOperationException("Could not start dotnet.");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, press) =>
        {
            press.Cancel = true;
            stop.Cancel();
        };

        Console.WriteLine($"Demo on {DemoHost.BaseUrl} - sign in as {MockData.Username} / {MockData.Password}. Ctrl+C stops it.");
        try
        {
            await process.WaitForExitAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        return 0;
    }

    public static async Task<string> RefreshDemoAsync(DemoPaths paths, CancellationToken cancellationToken)
    {
        if (PortProbe.IsListening(DemoDatabase.Port))
            throw new InvalidOperationException($"A demo is already running on {DemoHost.BaseUrl} - stop it first.");

        var admin = DemoDatabase.AdminConnectionString(DemoDatabase.DefaultCredentialFile);
        await Refresh.RunAsync(admin, DemoDatabase.Name, paths, cancellationToken);
        return DemoDatabase.For(admin, DemoDatabase.Name);
    }
}
