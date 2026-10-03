using AwesomeAssertions;
using Noof.Ledger.Demo.Shots;

namespace Noof.Ledger.Demo.Tests;

public sealed class GalleryTests
{
    [Fact]
    public void The_gallery_shows_every_screen_at_both_sizes_and_every_chat_scene()
    {
        var scenes = TelegramScenes.Build(TelegramScenes.CreateEcho());

        var markdown = Gallery.Render(AppScreens.All, scenes);

        foreach (var screen in AppScreens.All)
        {
            markdown.Should().Contain($"app/{screen.Name}-desktop.png");
            markdown.Should().Contain($"app/{screen.Name}-phone.png");
        }

        foreach (var scene in scenes)
            markdown.Should().Contain($"telegram/{scene.Name}.png");

        markdown.Should().NotContain("\r");
    }

    [Fact]
    public void Every_page_of_the_app_has_a_screen()
    {
        AppScreens.All.Select(screen => screen.Name).Should().Equal(
            "login", "dashboard", "dashboard-transfers", "wallets", "transactions", "trace", "trace-failed",
            "trace-exchange", "trace-foreign-spending", "trace-receipt", "trace-receipt-vision", "trace-receipt-check",
            "trace-slip-check", "diagnostics", "integrity", "logs", "log-settings", "secrets");
    }

    [Fact]
    public void The_screenshots_hide_a_findings_age_as_well_as_each_checks_time()
    {
        AppScreens.HideLiveValues.Should().Contain("#diagnostics-checks tbody td:nth-child(4) { visibility: hidden; }");
        AppScreens.HideLiveValues.Should().Contain(".noof-live-value { visibility: hidden; }");
    }
}
