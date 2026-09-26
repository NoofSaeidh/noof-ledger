using Microsoft.Playwright;

namespace Noof.Ledger.Demo.Shots;

internal sealed class ShotWriter(string screenshotsRoot, string sendRoot)
{
    const int MaxSliceDevicePixels = 4000;
    readonly List<string> changed = [];

    public IReadOnlyList<string> Changed => changed;

    public async Task SaveAsync(IPage page, string relativePath, Viewport viewport, string? hideCss)
    {
        var png = await page.ScreenshotAsync(Options(hideCss));
        if (!ShotFiles.WriteImageIfChanged(Path.Combine(screenshotsRoot, relativePath), png))
            return;

        changed.Add(relativePath);
        await SaveSendReadyAsync(page, relativePath, viewport, hideCss);
    }

    async Task SaveSendReadyAsync(IPage page, string relativePath, Viewport viewport, string? hideCss)
    {
        var height = await page.EvaluateAsync<int>("() => document.documentElement.scrollHeight");
        var slices = ShotFiles.Slices(height, (int)(MaxSliceDevicePixels / viewport.Scale));
        var stem = Path.ChangeExtension(relativePath, null).Replace('/', '-');

        for (var index = 0; index < slices.Count; index++)
        {
            var options = Options(hideCss);
            options.Clip = new Clip { X = 0, Y = slices[index].Y, Width = viewport.Width, Height = slices[index].Height };
            options.Type = ScreenshotType.Jpeg;
            options.Quality = 88;
            options.Path = Path.Combine(sendRoot, slices.Count == 1 ? $"{stem}.jpg" : $"{stem}-part{index + 1}.jpg");
            await page.ScreenshotAsync(options);
        }
    }

    static PageScreenshotOptions Options(string? hideCss) => new()
    {
        FullPage = true,
        Animations = ScreenshotAnimations.Disabled,
        Caret = ScreenshotCaret.Hide,
        Style = hideCss,
    };
}
