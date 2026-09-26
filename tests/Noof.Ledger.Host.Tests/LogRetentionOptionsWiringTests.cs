using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.Host.Tests;

// PR #1 review item 5, per-level retention: proves appsettings.json's Logging:Retention:Days
// section actually binds through the real host pipeline into the values EfLogRetention reads -
// not just that LogRetentionOptions' own C# property initializers hold the right defaults
// (EfLogRetentionTests already covers that against a real database).
public class LogRetentionOptionsWiringTests
{
    [Fact]
    public void The_configured_defaults_bind_from_the_real_appsettings_json()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseTempLogDirectory();
            builder.UseTempKeyRingDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

        var options = factory.Services.GetRequiredService<LogRetentionOptions>();

        options.Days.Verbose.Should().Be(1);
        options.Days.Debug.Should().Be(1);
        options.Days.Information.Should().Be(90);
        options.Days.Warning.Should().Be(90);
        options.Days.Error.Should().Be(90);
        options.Days.Fatal.Should().Be(90);
    }

    [Fact]
    public void A_configured_override_reaches_LogRetentionOptions()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseTempLogDirectory();
            builder.UseTempKeyRingDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.UseSetting("Logging:Retention:Days:Warning", "30");
        });

        var options = factory.Services.GetRequiredService<LogRetentionOptions>();

        options.Days.Warning.Should().Be(30);
        options.Days.Information.Should().Be(90, "an override for one level must not disturb another's default");
    }
}
