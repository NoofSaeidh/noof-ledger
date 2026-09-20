using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Noof.Ledger.E2E.Tests;

sealed partial class HostProcess : IAsyncDisposable
{
    Process? process;
    string? publishDirectory;

    public string BaseUrl { get; private set; } = string.Empty;

    public static async Task<string> PublishHostAsync(CancellationToken cancellationToken)
    {
        var repoRoot = RepoRoot.Locate();
        var hostProject = Path.Combine(repoRoot, "src", "Noof.Ledger.Host", "Noof.Ledger.Host.csproj");
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"noof-e2e-publish-{Guid.NewGuid():N}");

        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { "publish", hostProject, "-c", "Release", "-o", outputDirectory },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
        };

        using var publish = Process.Start(start)!;
        var stdoutTask = publish.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = publish.StandardError.ReadToEndAsync(cancellationToken);
        await publish.WaitForExitAsync(cancellationToken);

        if (publish.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet publish of Noof.Ledger.Host failed with exit code {publish.ExitCode}.\n" +
                $"{await stdoutTask}\n{await stderrTask}");
        }

        return outputDirectory;
    }

    public async Task StartAsync(
        string publishDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        this.publishDirectory = publishDirectory;
        process = Launch(publishDirectory, environment);

        try
        {
            BaseUrl = await WaitForListeningUrlAsync(process, TimeSpan.FromSeconds(30), cancellationToken);
            await WaitUntilReadyAsync(BaseUrl, TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    async Task StopAsync()
    {
        var toStop = process;
        process = null;

        if (toStop is not null)
        {
            try
            {
                if (!toStop.HasExited)
                {
                    toStop.Kill(entireProcessTree: true);
                    await toStop.WaitForExitAsync(CancellationToken.None);
                }
            }
            finally
            {
                toStop.Dispose();
            }
        }

        // WaitForExitAsync only guarantees the process object has reported exit - Windows can hold
        // the file/directory locks a launched process's working directory carries (every DLL it
        // loaded, and the directory itself as its CWD) for a short while after that. Best effort,
        // with a few retries for that gap: a stray publish directory in the OS temp folder is
        // disk-space hygiene, not a correctness or security concern like the process or the database.
        var toDelete = publishDirectory;
        publishDirectory = null;

        if (toDelete is not null && Directory.Exists(toDelete))
        {
            const int maxAttempts = 5;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Directory.Delete(toDelete, recursive: true);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == maxAttempts)
                        break;

                    await Task.Delay(TimeSpan.FromMilliseconds(200));
                }
            }
        }
    }

    static Process Launch(string publishDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var dll = Path.Combine(publishDirectory, "Noof.Ledger.Host.dll");

        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { dll, "--urls", "http://127.0.0.1:0" },
            // WebApplication.CreateBuilder derives ContentRootPath from the process's current
            // directory, not from the DLL's location. Leaving this unset made every static asset
            // resolve against the test runner's own working directory instead of the published
            // output - MapStaticAssets then answered every GET with 200 and an empty body (HEAD
            // stayed correct, since it only echoes the manifest's headers and never opens the file).
            // A real deployment always launches with its working directory set to its install
            // directory, so this also makes the fixture match production.
            WorkingDirectory = publishDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var (key, value) in environment)
            start.Environment[key] = value;

        var started = Process.Start(start) ?? throw new InvalidOperationException("Failed to start the host process.");
        started.BeginOutputReadLine();
        started.BeginErrorReadLine();
        return started;
    }

    static async Task<string> WaitForListeningUrlAsync(Process host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var urlSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();

        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (e.Data is null)
                return;

            lock (output)
                output.AppendLine(e.Data);

            var match = ListeningPattern().Match(e.Data);
            if (match.Success)
                urlSource.TrySetResult(match.Groups[1].Value);
        }

        host.OutputDataReceived += OnLine;
        host.ErrorDataReceived += OnLine;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            await using var registration = timeoutCts.Token.Register(() => urlSource.TrySetException(
                new TimeoutException($"Host did not report a listening URL within {timeout}. Output so far:\n{output}")));

            return await urlSource.Task;
        }
        finally
        {
            host.OutputDataReceived -= OnLine;
            host.ErrorDataReceived -= OnLine;
        }
    }

    static async Task WaitUntilReadyAsync(string baseUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            try
            {
                using var response = await client.GetAsync(baseUrl, timeoutCts.Token);
                if ((int)response.StatusCode is >= 200 and < 300)
                    return;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"Host at {baseUrl} did not become ready within {timeout}.");
            }
            catch
            {
                // Not ready yet - the port may not be accepting connections at all.
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Host at {baseUrl} did not become ready within {timeout}.");
            }
        }
    }

    [GeneratedRegex(@"Now listening on:\s*(\S+)")]
    private static partial Regex ListeningPattern();
}
