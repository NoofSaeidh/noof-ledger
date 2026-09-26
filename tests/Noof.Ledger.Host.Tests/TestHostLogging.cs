using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Noof.Ledger.Host.Logging;

namespace Noof.Ledger.Host.Tests;

// I-3 (Phase 5 final review): every WebApplicationFactory<Program> and every directly-spawned
// `dotnet Noof.Ledger.Host.dll` process in this project used to leave Logging:File:Directory
// unset, so a test host fell back to LoggingSetup's real default - %LOCALAPPDATA%\NoofLedger\logs,
// the operator's own log directory - and wrote test noise (including a locked-file rollover that
// can evict the operator's real files under retainedFileCountLimit) into it on every run. Every
// such host build must route through this helper instead of setting Logging:File:Directory (or the
// Logging__File__Directory environment variable) by hand.
public static class TestHostLogging
{
    public static string UseTempLogDirectory(this IWebHostBuilder builder)
    {
        var directory = Directory.CreateTempSubdirectory("noof-host-test-logs-").FullName;
        builder.UseSetting(LoggingSetup.DirectoryConfigKey, directory);
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
}
