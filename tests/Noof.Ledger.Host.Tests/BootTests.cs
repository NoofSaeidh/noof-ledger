using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Noof.Ledger.Host.Tests;

public class BootTests
{
    static WebApplicationFactory<Program> Factory(bool migrateOnStartup) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", migrateOnStartup.ToString());
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

    [Fact]
    public async Task An_anonymous_request_redirects_without_a_5xx_when_the_database_is_unreachable_and_migration_is_off()
    {
        using var factory = Factory(migrateOnStartup: false);
        using var client = factory.CreateClient();

        // Anonymous GET / redirects to /account/login, which renders without touching the
        // database, so this no longer exercises the authorized Home page's own database-down
        // handling - that guarantee now lives at the E2E layer (SmokeTests), where a real session
        // can reach it.
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Healthz_is_anonymous()
    {
        using var factory = Factory(migrateOnStartup: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("/healthz must stay anonymous");
    }

    [Fact]
    public async Task Stays_up_rather_than_failing_fast_when_migration_is_on_and_the_database_is_dead()
    {
        // Before the database gate (Phase 5 Task 1), Program.cs migrated synchronously before
        // app.Run(), so an unreachable database here threw before the host ever started serving.
        // DatabaseStartupService now runs as a hosted service instead, so the host starts and an
        // anonymous request still redirects to sign-in - the database being down is the gate's
        // problem to report (via DatabaseGateBanner), not a reason for the process to never come up.
        using var factory = Factory(migrateOnStartup: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();
    }
}
