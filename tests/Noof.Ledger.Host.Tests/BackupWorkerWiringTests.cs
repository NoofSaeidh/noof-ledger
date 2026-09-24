using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noof.Ledger.Application.Backup;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class BackupWorkerWiringTests
{
    static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

    [Fact]
    public void BackupWorker_is_registered_as_a_hosted_service()
    {
        using var factory = Factory();

        factory.Services.GetServices<IHostedService>().Should().Contain(service => service is BackupWorker);
    }

    [Fact]
    public void BackupWorkerOptions_is_a_singleton_with_its_documented_defaults()
    {
        using var factory = Factory();

        var options = factory.Services.GetRequiredService<BackupWorkerOptions>();

        options.Enabled.Should().BeTrue();
        options.Interval.Should().Be(TimeSpan.FromHours(24));
        options.RetryInterval.Should().Be(TimeSpan.FromHours(1));
        options.KeepCount.Should().Be(14);
        options.BackupDirectory.Should().EndWith(Path.Combine("NoofLedger", "backups"));
    }

    [Fact]
    public void Every_scoped_backup_port_resolves_without_touching_the_database()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IBackupLog>();
        scope.ServiceProvider.GetRequiredService<IDatabaseDumper>();
    }

    [Fact]
    public void A_disabled_backup_worker_is_not_registered()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.UseSetting("Backup:Enabled", "false");
        });

        factory.Services.GetServices<IHostedService>().Should().NotContain(service => service is BackupWorker);
    }
}
