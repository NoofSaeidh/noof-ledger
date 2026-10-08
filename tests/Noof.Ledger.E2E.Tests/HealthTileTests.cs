using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Fx;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.E2E.Tests;

public sealed class HealthTileTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task The_dashboard_shows_a_health_tile_linking_to_diagnostics()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var tile = Page.Locator("#health-tile");
        await Expect(tile).ToBeVisibleAsync();
        // Deliberately not asserting "All systems normal" vs a specific warning line here: this
        // clone has no Telegram token, no AI keys and no backup history configured, so which checks
        // are non-Ok is Task 6's own territory, not this task's to pin down. Only the tile's
        // presence and its link are this task's contract.
        await Expect(Page.Locator("#health-tile a[href='/diagnostics']")).ToBeVisibleAsync();
    }

    // Spec P-2: the fixture seeds synthetic rates for today before the host starts, so the host's FxRateWorker finds the
    // archive fresh and never calls open.er-api.com. Without the seed, today would hold either nothing or the real rates,
    // and neither equals the synthetic values.
    [Fact]
    public async Task The_clone_holds_todays_synthetic_rates_so_the_host_never_fetches()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var seededFor = fixture.RatesSeededFor;
        await using var db = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(fixture.ConnectionString).Options);

        var rates = await new EfFxRateStore(db, TimeProvider.System)
            .GetAsync(seededFor, seededFor, TestContext.Current.CancellationToken);

        rates.Where(rate => rate.AsOfDate == seededFor).Should().BeEquivalentTo(
            OpenErApiPayloads.Snapshot(seededFor).UnitsPerEur.Select(pair => new FxRate(pair.Key, seededFor, pair.Value)));
    }

    async Task SignInAsync()
    {
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");
        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");
    }
}
