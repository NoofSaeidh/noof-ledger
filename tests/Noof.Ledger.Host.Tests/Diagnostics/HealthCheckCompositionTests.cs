using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

public class HealthCheckCompositionTests
{
    static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseTempLogDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

    [Fact]
    public async Task The_seven_checks_are_registered_with_their_names_and_log_categories_in_display_order()
    {
        using var factory = Factory();
        var health = factory.Services.GetRequiredService<ISystemHealth>();

        var report = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

        report.Items.Select(item => (item.Name, item.LogCategory)).Should().BeEquivalentTo(
        [
            ("Database", "Noof.Ledger.Host.Startup.DatabaseStartupService"),
            ("Migrations", "Microsoft.EntityFrameworkCore.Migrations"),
            ("Telegram", "Noof.Ledger.Telegram"),
            ("AI keys", "Noof.Ledger.Ai"),
            ("Backup", "Noof.Ledger.Host.Workers.BackupWorker"),
            ("Disk", "Noof.Ledger.Host.Diagnostics"),
            ("Log sink", "Noof.Ledger.Host.Logging"),
        ], options => options.WithStrictOrdering());
    }
}
