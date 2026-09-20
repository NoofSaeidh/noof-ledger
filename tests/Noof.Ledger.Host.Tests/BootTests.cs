using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Noof.Ledger.Host.Tests;

public class BootTests
{
    static WebApplicationFactory<Program> Factory(string authMode, bool migrateOnStartup) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Mode", authMode);
            builder.UseSetting("Database:MigrateOnStartup", migrateOnStartup.ToString());
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

    [Fact]
    public async Task Boots_and_serves_with_the_database_unreachable_when_migration_is_off()
    {
        using var factory = Factory("Off", migrateOnStartup: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Cookie")]
    public async Task Healthz_is_anonymous_under_both_modes(string mode)
    {
        using var factory = Factory(mode, migrateOnStartup: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue($"/healthz must stay anonymous under Auth:Mode={mode}");
    }

    [Fact]
    public async Task Fails_fast_rather_than_hanging_when_migration_is_on_and_the_database_is_dead()
    {
        using var factory = Factory("Off", migrateOnStartup: true);

        var act = async () =>
        {
            using var client = factory.CreateClient();
            await client.GetAsync("/", TestContext.Current.CancellationToken);
        };

        await act.Should().ThrowAsync<Exception>();
    }
}
