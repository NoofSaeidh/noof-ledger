using System.Text;
using Microsoft.Playwright;

namespace Noof.Ledger.Demo.Shots;

internal static class ShotsCommand
{
    public static async Task<int> RunAsync()
    {
        var paths = DemoPaths.ForOperator();
        using var exclusive = DemoLock.Acquire(paths);
        var connectionString = await DemoCommands.RefreshDemoAsync(paths, CancellationToken.None);
        await using var host = await DemoHost.StartAsync(connectionString, paths, CancellationToken.None);

        if (Directory.Exists(RepoPaths.SendReady))
            Directory.Delete(RepoPaths.SendReady, recursive: true);
        Directory.CreateDirectory(RepoPaths.SendReady);

        var writer = new ShotWriter(RepoPaths.Screenshots, RepoPaths.SendReady);
        var scenes = TelegramScenes.Build(TelegramScenes.CreateEcho());

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchChromiumAsync(playwright);
        await AppShots.CaptureAsync(browser, writer);
        await TelegramShots.CaptureAsync(browser, writer, scenes);

        var removed = ShotFiles.RemoveStale(RepoPaths.Screenshots, writer.Saved);
        var galleryChanged = ShotFiles.WriteIfChanged(
            Path.Combine(RepoPaths.Screenshots, "README.md"), Encoding.UTF8.GetBytes(Gallery.Render(AppScreens.All, scenes)));

        Report(writer.Changed, removed, galleryChanged);
        return 0;
    }

    static async Task<IBrowser> LaunchChromiumAsync(IPlaywright playwright)
    {
        try
        {
            return await playwright.Chromium.LaunchAsync();
        }
        catch (PlaywrightException missing) when (missing.Message.Contains("Executable doesn't exist", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Chromium for Playwright is not installed. Run: pwsh {Path.Combine(AppContext.BaseDirectory, "playwright.ps1")} install chromium");
        }
    }

    static void Report(IReadOnlyList<string> changed, IReadOnlyList<string> removed, bool galleryChanged)
    {
        foreach (var path in removed)
            Console.WriteLine($"Removed {path}: no screen or scene takes it any more.");

        if (changed.Count == 0 && removed.Count == 0 && !galleryChanged)
        {
            Console.WriteLine("No screenshot changed.");
            return;
        }

        Console.WriteLine($"{changed.Count} screenshot(s) changed in docs\\screenshots:");
        foreach (var path in changed)
            Console.WriteLine($"  {path}");

        if (galleryChanged)
            Console.WriteLine("  README.md (gallery)");

        Console.WriteLine($"Phone-sized copies to send: {RepoPaths.SendReady}");
    }
}
