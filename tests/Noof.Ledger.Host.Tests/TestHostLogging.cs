using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Noof.Ledger.Host.Logging;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Host.Tests;

// I-3 (Phase 5 final review): every WebApplicationFactory<Program> and every directly-spawned
// `dotnet Noof.Ledger.Host.dll` process in this project used to leave Logging:File:Directory
// unset, so a test host fell back to LoggingSetup's real default - %LOCALAPPDATA%\NoofLedger\logs,
// the operator's own log directory - and wrote test noise (including a locked-file rollover that
// can evict the operator's real files under retainedFileCountLimit) into it on every run. Every
// such host build must route through this helper instead of setting Logging:File:Directory (or the
// Logging__File__Directory environment variable) by hand.
// Being the one call every in-process test host makes, the IWebHostBuilder overload also keeps it off open.er-api.com
// (TestHostRates).
public static class TestHostLogging
{
    public static string UseTempLogDirectory(this IWebHostBuilder builder)
    {
        var directory = Directory.CreateTempSubdirectory("noof-host-test-logs-").FullName;
        builder.UseSetting(LoggingSetup.DirectoryConfigKey, directory);
        builder.UseNoRemoteRates();
        return directory;
    }

    // A directly-spawned host process never reads IWebHostBuilder settings - it reads its own
    // environment, exactly like LoggingSetup.BuildBootstrapConfiguration does before the ASP.NET
    // Core configuration pipeline (and therefore any WebApplicationFactory-style override) exists.
    public static string UseTempLogDirectory(this ProcessStartInfo startInfo)
    {
        var directory = Directory.CreateTempSubdirectory("noof-host-test-logs-").FullName;
        startInfo.Environment["Logging__File__Directory"] = directory;
        return directory;
    }

    // A test that deletes its own temp log directory right after disposing an in-process
    // WebApplicationFactory<Program> races Serilog's rolling file sink: WebApplicationFactory's
    // DisposeAsync disposes the host's ILoggerFactory, but the file handle the sink held is not
    // always released by the time that call returns, so an immediate Directory.Delete can throw -
    // same class of Windows race HostProcess.DeleteBestEffortAsync already retries for the
    // out-of-process E2E host. Both delegate to the one shared implementation (Copilot finding, PR
    // #3: this copy used to fall short of that one - its final IOException escaped the filtered
    // catch, and it never caught UnauthorizedAccessException at all).
    public static Task DeleteBestEffortAsync(string? directory) => BestEffortDelete.DirectoryAsync(directory);
}
