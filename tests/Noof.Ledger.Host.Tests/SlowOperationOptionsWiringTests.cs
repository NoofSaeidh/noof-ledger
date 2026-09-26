using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Tests;

// Task V2: proves appsettings.json's Logging:SlowOperationMs section binds through the real host
// pipeline.
public class SlowOperationOptionsWiringTests
{
    static WebApplicationFactory<Program> Factory(Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseTempLogDirectory();
            builder.UseTempKeyRingDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            configure?.Invoke(builder);
        });

    [Fact]
    public void IOperationTimer_resolves_as_a_singleton()
    {
        using var factory = Factory();

        var first = factory.Services.GetRequiredService<IOperationTimer>();
        var second = factory.Services.GetRequiredService<IOperationTimer>();

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void A_configured_override_reaches_SlowOperationOptions()
    {
        using var factory = Factory(builder => builder.UseSetting("Logging:SlowOperationMs:MODEL", "45000"));

        var options = factory.Services.GetRequiredService<SlowOperationOptions>();

        options.ThresholdMs["model"].Should().Be(45000);
        options.ThresholdMs.Count(pair => string.Equals(pair.Key, "model", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, "case-insensitive lookup must not leave a duplicate key behind");
    }

    [Fact]
    public void The_configured_defaults_bind_from_the_real_appsettings_json()
    {
        using var factory = Factory();

        var options = factory.Services.GetRequiredService<SlowOperationOptions>();

        options.ThresholdMs["job.queueWait"].Should().Be(30000);
    }
}
