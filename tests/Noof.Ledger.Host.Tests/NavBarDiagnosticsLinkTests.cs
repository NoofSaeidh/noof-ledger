using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Noof.Ledger.Host.Tests;

public sealed class NavBarDiagnosticsLinkTests
{
    [Fact]
    public async Task The_nav_bar_links_to_diagnostics()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            // A safe, definitely-unreachable host - never the operator's real db.connection file
            // (LedgerConnectionString.Resolve falls back to it when ConnectionStrings:Ledger is
            // unset, which would otherwise let DatabaseStartupService dial noof_ledger itself; same
            // guard BootTests.cs already uses for the same reason).
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.ConfigureServices(services =>
            {
                FakeUserStore.Register(services);
                ReadyDatabaseGate.Register(services);
            });
        });

        // AllowAutoRedirect: false, same as LoginEndpointTests.Correct_credentials_set_an_auth_cookie
        // - otherwise the client follows the login POST's 302 straight into "/", which would query
        // the fake, unreachable database.
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        await LoginHelper.PostWithTokenAsync(client, "noof", "correct");
        // /wallets, not "/": it renders with prerender:false, so the static GET never runs the
        // page's own OnInitializedAsync (which would query the database) - only the NavBar
        // (statically rendered by MainLayout regardless of the page below it) plus a Blazor boot
        // marker.
        var html = await client.GetStringAsync("/wallets", TestContext.Current.CancellationToken);

        html.Should().Contain("href=\"/diagnostics\"");
        html.Should().Contain("Diagnostics");
    }
}
