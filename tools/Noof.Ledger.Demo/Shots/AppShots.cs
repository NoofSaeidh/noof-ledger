using Microsoft.Playwright;

namespace Noof.Ledger.Demo.Shots;

internal static class AppShots
{
    public static async Task CaptureAsync(IBrowser browser, ShotWriter writer)
    {
        foreach (var viewport in (Viewport[])[AppScreens.Phone, AppScreens.Desktop])
        {
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = viewport.Width, Height = viewport.Height },
                DeviceScaleFactor = viewport.Scale,
            });
            var page = await context.NewPageAsync();

            foreach (var screen in AppScreens.All.Where(screen => !screen.SignedIn))
                await ShootAsync(page, screen, viewport, writer);

            await SignInAsync(page);

            foreach (var screen in AppScreens.All.Where(screen => screen.SignedIn))
                await ShootAsync(page, screen, viewport, writer);
        }
    }

    static async Task ShootAsync(IPage page, AppScreen screen, Viewport viewport, ShotWriter writer)
    {
        await page.GotoAsync(DemoHost.BaseUrl + screen.Path);
        await page.Locator(screen.ReadyMarker).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        if (screen.Prepare is { } prepare)
        {
            await prepare(page);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        // Filling a field scrolls it into view, and a full-page shot taken scrolled down paints the
        // sticky navigation bar halfway down the image.
        await page.EvaluateAsync("() => window.scrollTo(0, 0)");

        await page.WaitForTimeoutAsync(500);
        await writer.SaveAsync(page, $"app/{screen.Name}-{viewport.Name}.png", viewport, AppScreens.HideLiveValues);
    }

    static async Task SignInAsync(IPage page)
    {
        await page.GotoAsync(DemoHost.BaseUrl + "/");
        await page.WaitForURLAsync("**/account/login*");
        await page.FillAsync("input[name='username']", MockData.Username);
        await page.FillAsync("input[name='password']", MockData.Password);
        await page.ClickAsync("button[type='submit']");
        await page.WaitForURLAsync(DemoHost.BaseUrl + "/");
    }
}
