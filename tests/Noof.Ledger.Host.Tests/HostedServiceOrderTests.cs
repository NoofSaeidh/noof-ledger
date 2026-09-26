using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noof.Ledger.Host.Diagnostics;
using Noof.Ledger.Host.Startup;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

// M-2 (Phase 5 final review): DatabaseStartupService gates every other Noof.Ledger hosted service
// through IDatabaseGate.WaitUntilReadyAsync, and the spec ("registered before every worker") and
// the branch's own contract call for it to be registered first among them - contract intent, not
// merely "eventually runs before the others block on it". AddNoofHostDiagnostics (which registers
// SecretSnapshotRefreshWorker) used to precede AddNoofDatabaseGate in Program.cs.
public class HostedServiceOrderTests
{
    [Fact]
    public void DatabaseStartupService_precedes_every_other_Noof_Ledger_worker()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseTempLogDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

        var hostedServices = factory.Services.GetServices<IHostedService>().ToArray();

        var databaseStartupIndex = Array.FindIndex(hostedServices, service => service is DatabaseStartupService);
        databaseStartupIndex.Should().BeGreaterThanOrEqualTo(0, "DatabaseStartupService must be registered");

        // TelegramPollingService is identified by name, not by reference, so this test does not
        // need InternalsVisibleTo from Noof.Ledger.Telegram (minimum accessibility - CLAUDE.md).
        var otherWorkerIndexes = hostedServices
            .Select((service, index) => (service, index))
            .Where(entry => entry.service is SecretSnapshotRefreshWorker or CategorizationWorker
                or TranscriptionWorker or LogRetentionWorker
                || entry.service.GetType().Name == "TelegramPollingService")
            .Select(entry => entry.index)
            .ToArray();

        otherWorkerIndexes.Should().NotBeEmpty();
        otherWorkerIndexes.Should().OnlyContain(index => index > databaseStartupIndex,
            "DatabaseStartupService must be registered before every other Noof.Ledger worker");
    }
}
