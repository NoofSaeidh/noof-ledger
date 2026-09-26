using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Diagnostics;
using Noof.Ledger.Host.Logging;
using Serilog;
using Serilog.Debugging;

namespace Noof.Ledger.Host.Tests;

// Serilog's SelfLog.Enable is one process-wide static delegate (SelfLogOwnership's own comment).
// Two hosts built in the same process - most concretely, WebApplicationFactory's HostFactoryResolver
// building and disposing a throwaway host to probe a minimal-hosting Program.cs before building the
// real one - used to leave the first host's ILogSinkStatus wired to that shared delegate after it
// was gone, so a sink failure reported once the second host had taken over could still land on the
// first host's already-disposed status. SelfLogSinkFailureTests reproduced exactly this as a flake
// (fix-fd-report.md), passing most runs and failing `sinkStatus.LastFailureAt.Should().BeNull()`
// before the test's own probe had even logged anything - traced to an *orphaned* host (never
// disposed at all) whose own Postgres sink kept retrying, and landing on whoever was current,
// throughout the rest of the run.
public class SelfLogOwnershipTests
{
    const string UnreachableConnectionString = "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2";
    const string OtherUnreachableConnectionString = "Host=127.0.0.1;Port=40001;Database=never_dialled;Username=none;Timeout=2";

    [Fact]
    public void A_disposed_hosts_status_never_receives_a_failure_reported_after_it_let_go()
    {
        var firstStatus = new LogSinkStatus();
        var firstRegistration = SelfLogOwnership.Claim(firstStatus, TimeProvider.System, UnreachableConnectionString);

        firstRegistration.Dispose();

        SelfLog.WriteLine("synthetic failure after the owning host disposed");

        firstStatus.LastFailureAt.Should().BeNull(
            "a host that already let go of the shared SelfLog delegate must not still be fed by it");
    }

    [Fact]
    public void A_live_claim_still_receives_its_own_failure()
    {
        var status = new LogSinkStatus();
        using var registration = SelfLogOwnership.Claim(status, TimeProvider.System, UnreachableConnectionString);

        SelfLog.WriteLine("synthetic failure while the claim is still live");

        status.LastFailureAt.Should().NotBeNull("a claim that never disposed must still receive its own failures");
    }

    [Fact]
    public void A_later_claim_receives_its_own_failure_while_the_earlier_ones_status_stays_untouched()
    {
        var firstStatus = new LogSinkStatus();
        var firstRegistration = SelfLogOwnership.Claim(firstStatus, TimeProvider.System, UnreachableConnectionString);
        firstRegistration.Dispose();

        var secondStatus = new LogSinkStatus();
        using var secondRegistration = SelfLogOwnership.Claim(secondStatus, TimeProvider.System, UnreachableConnectionString);

        SelfLog.WriteLine("synthetic failure while the second claim owns the delegate");

        secondStatus.LastFailureAt.Should().NotBeNull("the current claim must still receive its own failures");
        firstStatus.LastFailureAt.Should().BeNull("the disposed, earlier claim must never be fed again");
    }

    // The actual mechanism behind the flake: an orphaned claim - never disposed, so the compare-and-
    // clear layer above cannot touch it - keeps reporting connection failures for its own address
    // long after some other, live claim has taken over. Content is the only thing left to tell them
    // apart: a "Failed to connect to <host>:<port>" message names an address, and one that names a
    // *different* address than the current claim's own can never be that claim's own failure.
    [Fact]
    public void A_failure_naming_a_different_connection_never_reaches_the_current_claim()
    {
        var status = new LogSinkStatus();
        using var registration = SelfLogOwnership.Claim(status, TimeProvider.System, OtherUnreachableConnectionString);

        SelfLog.WriteLine("Npgsql.NpgsqlException (0x80004005): Failed to connect to 127.0.0.1:59999");

        status.LastFailureAt.Should().BeNull("the message names a different host:port than this claim's own connection");
    }

    [Fact]
    public void A_failure_naming_the_current_claims_own_connection_is_recorded()
    {
        var status = new LogSinkStatus();
        using var registration = SelfLogOwnership.Claim(status, TimeProvider.System, OtherUnreachableConnectionString);

        SelfLog.WriteLine("Npgsql.NpgsqlException (0x80004005): Failed to connect to 127.0.0.1:40001");

        status.LastFailureAt.Should().NotBeNull("the message names exactly this claim's own connection");
    }

    [Fact]
    public void A_failure_that_names_no_connection_at_all_still_defaults_to_the_current_claim()
    {
        var status = new LogSinkStatus();
        using var registration = SelfLogOwnership.Claim(status, TimeProvider.System, OtherUnreachableConnectionString);

        SelfLog.WriteLine("relation \"app_log\" does not exist");

        status.LastFailureAt.Should().NotBeNull(
            "a failure that names no specific connection keeps defaulting to whoever is current, exactly as before");
    }

    // The end-to-end shape of the actual flake: LoggingSetup.Configure called for one host, that
    // host's logger disposed (as WebApplicationFactory disposes a host on shutdown), then Configure
    // called again for a second host - mirroring LogLevelConfigurationTests' own pattern of driving
    // LoggingSetup.Configure directly rather than through a full WebApplicationFactory.
    [Fact]
    public void Disposing_one_hosts_logger_never_leaves_the_next_hosts_sink_status_pre_failed()
    {
        var firstLogDirectory = Directory.CreateTempSubdirectory("noof-selflog-ownership-test-").FullName;
        var secondLogDirectory = Directory.CreateTempSubdirectory("noof-selflog-ownership-test-").FullName;

        try
        {
            var firstStatus = new LogSinkStatus();
            var firstConfiguration = new LoggerConfiguration();
            LoggingSetup.Configure(
                firstConfiguration, new ConfigurationBuilder().Build(), firstLogDirectory,
                UnreachableConnectionString, BuildFakeServices(firstStatus));
            var firstLogger = firstConfiguration.CreateLogger();
            firstLogger.Dispose();

            SelfLog.WriteLine("synthetic failure after the first host's logger disposed");

            var secondStatus = new LogSinkStatus();
            var secondConfiguration = new LoggerConfiguration();
            LoggingSetup.Configure(
                secondConfiguration, new ConfigurationBuilder().Build(), secondLogDirectory,
                UnreachableConnectionString, BuildFakeServices(secondStatus));

            firstStatus.LastFailureAt.Should().BeNull(
                "disposing the first host's logger must release its SelfLog claim through the real " +
                "LoggingSetup wiring, not just leave the shared delegate pointing at its now-disposed status");
        }
        finally
        {
            Directory.Delete(firstLogDirectory, recursive: true);
            Directory.Delete(secondLogDirectory, recursive: true);
        }
    }

    static IServiceProvider BuildFakeServices(ILogSinkStatus sinkStatus)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDatabaseGate>(new AlwaysReadyGate());
        services.AddSingleton(sinkStatus);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretValueSource>(new NoSecrets());
        services.AddSingleton<SecretRedactor>();
        return services.BuildServiceProvider();
    }

    sealed class AlwaysReadyGate : IDatabaseGate
    {
        public DatabaseState State => DatabaseState.Ready;
        public string? Detail => null;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    sealed class NoSecrets : ISecretValueSource
    {
        public IReadOnlyCollection<string> CurrentValues => [];
    }
}
