using System.Collections.Concurrent;
using System.Diagnostics;

namespace Noof.Ledger.Demo;

internal sealed class DemoHost(Process process) : IAsyncDisposable
{
    static readonly TimeSpan StartupLimit = TimeSpan.FromMinutes(3);

    public static string BaseUrl => $"http://127.0.0.1:{DemoDatabase.Port}";

    public static ProcessStartInfo StartInfo(string connectionString, DemoPaths paths, bool redirect)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = RepoPaths.Root.FullName,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
        };
        foreach (var argument in (string[])["run", "--project", RepoPaths.Host, "-c", "Release", "--no-launch-profile"])
            start.ArgumentList.Add(argument);

        start.Environment["ConnectionStrings__Ledger"] = connectionString;
        start.Environment["Urls"] = BaseUrl;
        start.Environment["Logging__File__Directory"] = paths.Logs;
        start.Environment["DataProtection__KeyRingDirectory"] = paths.KeyRing;
        start.Environment["Backup__Enabled"] = "false";
        return start;
    }

    public static async Task<DemoHost> StartAsync(string connectionString, DemoPaths paths, CancellationToken cancellationToken)
    {
        var recent = new ConcurrentQueue<string>();
        var process = Process.Start(StartInfo(connectionString, paths, redirect: true))
            ?? throw new InvalidOperationException("Could not start dotnet.");
        process.OutputDataReceived += (_, line) => Remember(recent, line.Data);
        process.ErrorDataReceived += (_, line) => Remember(recent, line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var host = new DemoHost(process);
        try
        {
            await WaitUntilReadyAsync(process, recent, cancellationToken);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);

        await process.WaitForExitAsync();
        process.Dispose();
    }

    static async Task WaitUntilReadyAsync(Process process, ConcurrentQueue<string> recent, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + StartupLimit;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"The demo host exited early:\n{string.Join('\n', recent)}");

            try
            {
                var page = await http.GetStringAsync($"{BaseUrl}/account/login", cancellationToken);
                if (!page.Contains("id=\"database-waiting\"", StringComparison.Ordinal))
                    return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException($"The demo host did not come up within {StartupLimit}:\n{string.Join('\n', recent)}");
    }

    static void Remember(ConcurrentQueue<string> recent, string? line)
    {
        if (line is null)
            return;

        recent.Enqueue(line);
        while (recent.Count > 40)
            recent.TryDequeue(out _);
    }
}
