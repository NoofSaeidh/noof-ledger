using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Noof.Ledger.Host.Tests;

// The theme is the one part of the look that fails without a symptom anyone can act on. MudBlazor
// writes each font family into a CSS variable already wrapped in single quotes, so a family list
// handed to it as one comma-separated string arrives as a single font name nobody has, and every
// page renders in the browser's default serif. Nothing throws, nothing logs, every test passes, and
// the only detector is a person who knows what the app is supposed to look like. This is that
// detector - it reads the variable off the page the app actually serves.
public partial class ThemeTests
{
    [GeneratedRegex(@"--mud-typography-default-family:\s*(?<value>[^;]+);")]
    private static partial Regex DefaultFontFamily { get; }

    static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.ConfigureServices(FakeUserStore.Register);
        });

    static async Task<string> FontFamilyAsync()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/account/login", TestContext.Current.CancellationToken);
        var match = DefaultFontFamily.Match(html);

        match.Success.Should().BeTrue(
            "the theme provider must emit its typography variables into the served page; if this "
            + "variable is missing entirely, MudThemeProvider is not rendering at all");

        return match.Groups["value"].Value;
    }

    [Fact]
    public async Task The_font_family_the_page_serves_is_a_list_of_families_not_one_impossible_name()
    {
        var family = await FontFamilyAsync();

        // The broken form is 'a, b, c' - one quoted run containing commas. The correct form is
        // 'a', 'b', 'c' - every comma sits outside the quotes.
        var insideQuotes = Regex.Matches(family, "'([^']*)'").Select(m => m.Groups[1].Value);

        insideQuotes.Should().AllSatisfy(name => name.Should().NotContain(",",
            "a quoted font family containing commas is one font name no machine has, so the page "
            + "silently renders in the browser's default serif instead"));
    }

    [Fact]
    public async Task The_page_asks_for_a_font_this_machine_could_actually_have()
    {
        var family = await FontFamilyAsync();

        family.Should().Contain("system-ui",
            "the whole point of choosing a system font stack was to avoid fetching a typeface from "
            + "the internet on every page load; losing the system families quietly undoes that");
    }
}
