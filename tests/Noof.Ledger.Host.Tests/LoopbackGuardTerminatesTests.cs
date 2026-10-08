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
        string? logDirectory = null;
        try
        {
            using var host = StartHost("http://0.0.0.0:0", out logDirectory);

            var exited = await BuiltHostProcess.WaitForExitAsync(host, TimeSpan.FromSeconds(30));

            exited.Should().BeTrue("the host must refuse to serve, not merely log and carry on");
            host.ExitCode.Should().Be(1, "a safety refusal must be detectable by a service manager");
        }
        finally
        {
            if (logDirectory is not null && Directory.Exists(logDirectory))
                Directory.Delete(logDirectory, recursive: true);
        }
    }

    static Process StartHost(string urls, out string logDirectory)
    {
        var start = BuiltHostProcess.StartInfo(["--urls", urls], out logDirectory);
        start.RedirectStandardError = true;

        start.Environment["Database__MigrateOnStartup"] = "false";
        start.Environment["ConnectionStrings__Ledger"] =
            "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2";

        return Process.Start(start)!;
    }
}
