using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Noof.Ledger.E2E.Tests;

sealed partial class HostProcess : IAsyncDisposable
{
    readonly List<string> capturedOutputLines = [];
    readonly Lock captureLock = new();

    Process? process;
    string? publishDirectory;
    string? logDirectory;

    public string BaseUrl { get; private set; } = string.Empty;

    public IReadOnlyList<string> CapturedOutputLines
    {
        get { lock (captureLock) { return [.. capturedOutputLines]; } }
    }

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

    // waitForDatabaseReady defaults to true: almost every fixture points at a database that will
    // come up, and Login.razor is statically rendered - it shows DatabaseGateBanner
    // (id="database-waiting") instead of the sign-in form until DatabaseStartupService's gate opens,
    // and never re-renders itself once served. A plain HTTP 200 on "/" only proves Kestrel is
    // listening, not that the gate is Ready, so a caller that skipped this wait and went straight to
    // SignInAsync could hit the banner instead of the form under load. UnreachableDatabaseHostFixture
    // is the one caller that passes false: its database never becomes reachable, so waiting for the
    // banner to disappear would just run out the clock.
    // workingDirectory defaults to publishDirectory - a real deployment always launches from its
    // own install directory. Task 11's ContentRootIndependenceTests is the one caller that passes
    // something else, to prove the content root no longer depends on it.
    public async Task StartAsync(
        string publishDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken,
        bool waitForDatabaseReady = true, string? workingDirectory = null)
    {
        this.publishDirectory = publishDirectory;
        var (mergedEnvironment, createdLogDirectory) = WithTempLogDirectory(environment);
        logDirectory = createdLogDirectory;
        process = Launch(publishDirectory, mergedEnvironment, workingDirectory ?? publishDirectory);
        process.OutputDataReceived += CaptureLine;
        process.ErrorDataReceived += CaptureLine;

        try
        {
            BaseUrl = await WaitForListeningUrlAsync(process, TimeSpan.FromSeconds(30), cancellationToken);

            if (waitForDatabaseReady)
                await WaitUntilLoginReadyAsync(BaseUrl, TimeSpan.FromSeconds(60), cancellationToken);
            else
                await WaitUntilRespondingAsync(BaseUrl, TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // I-3 (Phase 5 final review): a spawned host that never overrides Logging:File:Directory falls
    // back to LoggingSetup's real default - %LOCALAPPDATA%\NoofLedger\logs, the operator's own log
    // directory - and pollutes it with test noise. Every host this fixture launches gets a private
    // temp directory instead, unless the caller already named one (none do today). A pure static
    // method, separate from the instance that owns cleanup, so the merge itself is a fast test
    // (HostProcessTests) with no process to spawn.
    internal static (IReadOnlyDictionary<string, string> Environment, string? CreatedLogDirectory) WithTempLogDirectory(
        IReadOnlyDictionary<string, string> environment)
    {
        if (environment.ContainsKey("Logging__File__Directory"))
            return (environment, null);

        var directory = Directory.CreateTempSubdirectory("noof-e2e-host-logs-").FullName;
        return (new Dictionary<string, string>(environment) { ["Logging__File__Directory"] = directory }, directory);
    }

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
                    // CancellationToken.None deliberately: a cancelled test run must still wait for
                    // the killed process to actually exit, or it leaks a process instead of a database.
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
        // with a few retries for that gap: a stray publish or log directory in the OS temp folder is
        // disk-space hygiene, not a correctness or security concern like the process or the database.
        var publishToDelete = publishDirectory;
        publishDirectory = null;
        await DeleteBestEffortAsync(publishToDelete);

        var logToDelete = logDirectory;
        logDirectory = null;
        await DeleteBestEffortAsync(logToDelete);
    }

    static async Task DeleteBestEffortAsync(string? path)
    {
        if (path is null || !Directory.Exists(path))
            return;

        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxAttempts)
                    return;

                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
        }
    }

    void CaptureLine(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
            return;

        lock (captureLock)
            capturedOutputLines.Add(e.Data);
    }

    static Process Launch(string publishDirectory, IReadOnlyDictionary<string, string> environment, string workingDirectory)
    {
        var dll = Path.Combine(publishDirectory, "Noof.Ledger.Host.dll");

        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { dll, "--urls", "http://127.0.0.1:0" },
            // Program.cs now sets ContentRootPath from AppContext.BaseDirectory (Task 11), not the
            // process's current directory, so this fixture's default (publishDirectory, matching a
            // real deployment) and ContentRootIndependenceTests's deliberately different directory
            // must both resolve static assets correctly.
            WorkingDirectory = workingDirectory,
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

    static async Task WaitUntilRespondingAsync(string baseUrl, TimeSpan timeout, CancellationToken cancellationToken)
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

    // Polls /account/login itself, not "/" (which 302s there anyway), until it comes back without
    // DatabaseGateBanner's id="database-waiting" - i.e. until the database gate this login page was
    // served under was already Ready, not merely until Kestrel answers something.
    static async Task WaitUntilLoginReadyAsync(string baseUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var loginUrl = baseUrl.TrimEnd('/') + "/account/login";

        while (true)
        {
            try
            {
                using var response = await client.GetAsync(loginUrl, timeoutCts.Token);
                if ((int)response.StatusCode is >= 200 and < 300)
                {
                    var html = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                    if (!html.Contains("id=\"database-waiting\"", StringComparison.Ordinal))
                        return;
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Host at {loginUrl} did not become ready (database gate never opened) within {timeout}.");
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
                throw new TimeoutException(
                    $"Host at {loginUrl} did not become ready (database gate never opened) within {timeout}.");
            }
        }
    }

    [GeneratedRegex(@"Now listening on:\s*(\S+)")]
    private static partial Regex ListeningPattern();
}
