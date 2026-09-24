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
    public async Task Fails_fast_rather_than_hanging_when_migration_is_on_and_the_database_is_dead()
    {
        using var factory = Factory(migrateOnStartup: true);

        var act = async () =>
        {
            using var client = factory.CreateClient();
            await client.GetAsync("/", TestContext.Current.CancellationToken);
        };

        await act.Should().ThrowAsync<Exception>();
    }
}
