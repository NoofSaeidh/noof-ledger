using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Noof.Ledger.Host.Tests;

// Task 11: `run.ps1 start` runs Production from source, not from a publish, and static web assets
// are only wired in automatically for the Development environment - Program.cs must call
// builder.WebHost.UseStaticWebAssets() itself, or the sign-in page's stylesheet and Blazor script
// serve the same 200-with-empty-body the RUNBOOK's "wrong current directory" bug describes.
public sealed partial class ProductionStaticAssetsTests
{
    static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseTempLogDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

    [Fact]
    public async Task The_sign_in_page_s_stylesheet_and_blazor_script_are_served_non_empty_in_Production()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/account/login", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        html.Should().Contain("_framework/blazor.web.js");

        var match = AppCssHrefPattern().Match(html);
        match.Success.Should().BeTrue("the sign-in page must reference its own (possibly fingerprinted) stylesheet");

        var cssResponse = await client.GetAsync("/" + match.Groups[1].Value, TestContext.Current.CancellationToken);
        cssResponse.EnsureSuccessStatusCode();

        var cssBytes = await cssResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        cssBytes.Should().NotBeEmpty(
            "static web assets not wired in for Production would answer 200 with an empty body");
    }

    [Fact]
    public async Task The_download_script_is_served_non_empty_in_Production()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/account/login", TestContext.Current.CancellationToken);
        var match = DownloadScriptSrcPattern().Match(html);
        match.Success.Should().BeTrue("every page references the bug reports' download helper from App.razor");

        var script = await client.GetStringAsync("/" + match.Groups[1].Value, TestContext.Current.CancellationToken);

        script.Should().Contain("noofLedger.downloadText",
            "static web assets not wired in for Production would answer 200 with an empty body");
    }

    [GeneratedRegex(@"href=""(_content/Noof\.Ledger\.Web/app[^""]*\.css)""")]
    private static partial Regex AppCssHrefPattern();

    [GeneratedRegex(@"src=""(_content/Noof\.Ledger\.Web/download[^""]*\.js)""")]
    private static partial Regex DownloadScriptSrcPattern();
}
