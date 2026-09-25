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
            builder.UseTempLogDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            // M-8 (Phase 4 final review): this factory leaves Backup:Enabled on, so its
            // BackupWorker actually runs as a hosted service. Its safety must not depend solely on
            // port 59999 always refusing the connection before RunBackupAsync ever touches a
            // directory - a reachable database here must still never be able to make it write into
            // the operator's real %LOCALAPPDATA%\NoofLedger\backups.
            builder.UseSetting("Backup:BackupDirectory",
                Path.Combine(Path.GetTempPath(), $"noof-backup-wiring-tests-{Guid.NewGuid():N}"));
        });

    [Fact]
    public void BackupWorker_is_registered_as_a_hosted_service()
    {
        using var factory = Factory();

        factory.Services.GetServices<IHostedService>().Should().Contain(service => service is BackupWorker);
    }

    [Fact]
    public void BackupWorkerOptions_defaults_to_the_operators_folder_under_LocalApplicationData()
    {
        new BackupWorkerOptions().BackupDirectory.Should().EndWith(Path.Combine("NoofLedger", "backups"));
    }

    [Fact]
    public void BackupWorkerOptions_is_a_singleton_bound_from_configuration()
    {
        using var factory = Factory();

        var options = factory.Services.GetRequiredService<BackupWorkerOptions>();

        options.Enabled.Should().BeTrue();
        options.Interval.Should().Be(TimeSpan.FromHours(24));
        options.RetryInterval.Should().Be(TimeSpan.FromHours(1));
        options.KeepCount.Should().Be(14);
        options.BackupDirectory.Should().StartWith(Path.GetTempPath(),
            "this factory overrides it away from the operator's real backup directory (M-8)");
        factory.Services.GetRequiredService<BackupWorkerOptions>().Should().BeSameAs(options);
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
            builder.UseTempLogDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.UseSetting("Backup:Enabled", "false");
        });

        factory.Services.GetServices<IHostedService>().Should().NotContain(service => service is BackupWorker);
    }
}
