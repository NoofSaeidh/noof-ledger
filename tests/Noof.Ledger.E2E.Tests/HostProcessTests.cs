using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Noof.Ledger.Host.Logging;

namespace Noof.Ledger.E2E.Tests;

// I-3 (Phase 5 final review): guards HostProcess.WithTempLogDirectory - the merge every fixture
// (CookieModeHostFixture, UnreachableDatabaseHostFixture, DiagnosticsLogsTests's own HostProcess)
// relies on to keep a spawned host's logs out of the operator's real log directory.
public sealed class HostProcessTests
{
    [Fact]
    public void A_caller_that_names_no_log_directory_gets_one_under_the_OS_temp_path_never_the_real_default()
    {
        // This suite (like the app it drives) only ever runs on Windows - see
        // CookieModeHostFixture.ReadStoredSecretAsync for the same guard against the same
        // assembly-level [SupportedOSPlatform("windows")] on Noof.Ledger.Host.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("This suite only runs on Windows, same as the app.");

        var (environment, createdDirectory) = HostProcess.WithTempLogDirectory(new Dictionary<string, string>
        {
            ["Database__MigrateOnStartup"] = "false",
        });

        try
        {
            createdDirectory.Should().NotBeNull();
            environment.Should().ContainKey("Logging__File__Directory").WhoseValue.Should().Be(createdDirectory);

            var fullPath = Path.GetFullPath(createdDirectory!);
            fullPath.Should().StartWith(Path.GetFullPath(Path.GetTempPath()),
                "a test host must never resolve LoggingSetup's real default directory");

            var realDefault = LoggingSetup.ResolveLogDirectory(new ConfigurationBuilder().Build());
            fullPath.Should().NotBe(realDefault);
        }
        finally
        {
            if (createdDirectory is not null && Directory.Exists(createdDirectory))
                Directory.Delete(createdDirectory, recursive: true);
        }
    }

    [Fact]
    public void A_caller_that_already_names_a_log_directory_is_left_alone()
    {
        var explicitEnvironment = new Dictionary<string, string> { ["Logging__File__Directory"] = @"C:\already-set" };

        var (environment, createdDirectory) = HostProcess.WithTempLogDirectory(explicitEnvironment);

        createdDirectory.Should().BeNull();
        environment.Should().BeSameAs(explicitEnvironment);
    }
}
