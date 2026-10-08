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

            var exited = await BuiltHostProcess.WaitForExitAsync(host, TimeSpan.FromSeconds(30));

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
        var start = BuiltHostProcess.StartInfo(["--urls", "http://127.0.0.1:0"], out logDirectory);
        start.RedirectStandardError = true;

        start.Environment["Database__MigrateOnStartup"] = "false";
        start.Environment["Capture__TimeZone"] = "Not/AZone";

        return Process.Start(start)!;
    }
}
