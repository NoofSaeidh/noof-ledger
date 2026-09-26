using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Tests;

public class SelfLogSinkFailureTests
{
    [Fact]
    public async Task A_real_Postgres_sink_write_failure_sets_LastFailureAt_through_SelfLog()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseTempLogDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.ConfigureServices(ReadyDatabaseGate.Register);
        });

        using var client = factory.CreateClient();

        var sinkStatus = factory.Services.GetRequiredService<ILogSinkStatus>();
        sinkStatus.LastFailureAt.Should().BeNull("nothing has tried to write to the (unreachable) sink yet");

        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Noof.Ledger.Host.Tests.SelfLogProbe");
        logger.LogInformation("Probing the Postgres sink at {At}", DateTimeOffset.UtcNow);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (sinkStatus.LastFailureAt is null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(250, TestContext.Current.CancellationToken);

        sinkStatus.LastFailureAt.Should().NotBeNull(
            "a write against an unreachable PostgreSQL sink must reach ILogSinkStatus through Serilog's SelfLog wiring");
    }
}
