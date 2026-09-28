using Microsoft.Playwright;

namespace Noof.Ledger.Demo.Shots;

internal static class TelegramShots
{
    public static async Task CaptureAsync(IBrowser browser, ShotWriter writer, IReadOnlyList<ChatScene> scenes)
    {
        var phone = AppScreens.Phone;
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = phone.Width, Height = phone.Height },
            DeviceScaleFactor = phone.Scale,
        });
        var page = await context.NewPageAsync();

        foreach (var scene in scenes)
        {
            await page.SetContentAsync(TelegramChatPage.Render(scene));
            await writer.SaveAsync(page, $"telegram/{scene.Name}.png", phone, hideCss: null);
        }
    }
}
