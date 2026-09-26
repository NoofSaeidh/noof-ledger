using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Noof.Ledger.Host.Tests;

// Native <select> and date inputs are drawn partly by the operating system, and in a dark app they
// fail the way the font once did: nothing errors, every test passes, and the dropdown list, the
// calendar popup and the picker icon come out light-on-light or black-on-black. These tests read the
// stylesheet and theme variables the app actually serves - a source-text assertion could pass while
// the served page stayed broken.
public sealed partial class ServedStylesTests
{
    [GeneratedRegex(@"href=""(_content/Noof\.Ledger\.Web/app[^""]*\.css)""")]
    private static partial Regex AppCssHref { get; }

    [GeneratedRegex(@"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex CssRule { get; }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment { get; }

    sealed record Served(string Html, string Css);

    sealed record Rule(IReadOnlyList<string> Selectors, string Body);

    static async Task<Served> ServedAsync()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseTempLogDirectory();
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
            builder.ConfigureServices(FakeUserStore.Register);
        });
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        var html = await client.GetStringAsync("/account/login", cancellationToken);
        var href = AppCssHref.Match(html);
        href.Success.Should().BeTrue("the page must link the app's own stylesheet");

        var css = await client.GetStringAsync("/" + href.Groups[1].Value, cancellationToken);
        return new Served(html, CssComment.Replace(css, string.Empty));
    }

    static IReadOnlyList<Rule> Rules(string css) =>
    [
        .. CssRule.Matches(css).Select(match => new Rule(
            [.. match.Groups["selectors"].Value.Split(',').Select(selector => Regex.Replace(selector.Trim(), @"\s+", " "))],
            match.Groups["body"].Value)),
    ];

    static IEnumerable<string> BodiesFor(string css, string selector) =>
        Rules(css).Where(rule => rule.Selectors.Contains(selector)).Select(rule => rule.Body);

    static string ThemeVariable(string html, string name)
    {
        var match = Regex.Match(html, $@"--{Regex.Escape(name)}:\s*(?<value>[^;]+);");
        match.Success.Should().BeTrue($"the theme provider must emit --{name}");
        return match.Groups["value"].Value.Trim();
    }

    [Fact]
    public async Task The_document_asks_for_native_controls_in_the_theme_s_own_colour_scheme()
    {
        var served = await ServedAsync();

        ThemeVariable(served.Html, "mud-native-html-color-scheme").Should().Be("dark",
            "NoofTheme is dark-only; the theme provider publishes that as this variable");

        BodiesFor(served.Css, ":root").Should().Contain(body => body.Contains("color-scheme: var(--mud-native-html-color-scheme"),
            "without color-scheme on the root, Chromium draws the <select> dropdown list, the date "
            + "picker's calendar popup and its icon for a light page - light lists and a black icon on a "
            + "dark field. Reading the theme's own variable keeps it right if the theme ever changes");
    }

    [Fact]
    public async Task A_native_select_draws_a_theme_coloured_chevron_instead_of_the_OS_arrow()
    {
        var served = await ServedAsync();

        var select = string.Join("\n", BodiesFor(served.Css, ".noof-native-select"));

        select.Should().Contain("appearance: none",
            "the OS arrow ignores the theme and sits at a different inset from every other control");
        select.Should().MatchRegex(@"background-image:[^;]*var\(--noof-chevron",
            "with the OS arrow gone, the chevron must come from a colour the theme decides");
    }

    [Fact]
    public async Task A_native_select_s_options_use_the_theme_surface_and_text_colours()
    {
        var served = await ServedAsync();

        var options = string.Join("\n", BodiesFor(served.Css, ".noof-native-select option"));

        options.Should().Contain("background-color: var(--mud-palette-surface)");
        options.Should().Contain("color: var(--mud-palette-text-primary)");
    }

    [Fact]
    public async Task Text_inputs_and_selects_share_one_rule_for_height_border_and_radius()
    {
        var served = await ServedAsync();

        var shared = Rules(served.Css)
            .Where(rule => rule.Selectors.Contains(".noof-input") && rule.Selectors.Contains(".noof-native-select"))
            .Select(rule => rule.Body)
            .ToList();

        shared.Should().Contain(body => body.Contains("height:") && body.Contains("border:") && body.Contains("border-radius:"),
            "two rules kept in step by hand drift apart - the filter bars once showed a 42px select "
            + "beside a 44px date field on the same row");
    }

    [Fact]
    public async Task Keyboard_focus_draws_a_visible_ring()
    {
        var served = await ServedAsync();

        Rules(served.Css)
            .Where(rule => rule.Selectors.Any(selector => selector.EndsWith(":focus-visible", StringComparison.Ordinal)))
            .Should().Contain(rule => rule.Body.Contains("outline:") && rule.Body.Contains("var(--mud-palette-primary)"));
    }

    [Theory]
    [InlineData("mud-palette-text-primary", "mud-palette-background", 4.5)]
    [InlineData("mud-palette-text-primary", "mud-palette-surface", 4.5)]
    [InlineData("mud-palette-text-secondary", "mud-palette-background", 4.5)]
    [InlineData("mud-palette-text-secondary", "mud-palette-surface", 4.5)]
    [InlineData("mud-palette-primary", "mud-palette-background", 4.5)]
    [InlineData("mud-palette-primary", "mud-palette-surface", 4.5)]
    [InlineData("mud-palette-error", "mud-palette-surface", 4.5)]
    [InlineData("mud-palette-warning", "mud-palette-surface", 4.5)]
    [InlineData("mud-palette-primary-text", "mud-palette-primary", 4.5)]
    [InlineData("mud-palette-success-text", "mud-palette-success", 4.5)]
    [InlineData("mud-palette-error-text", "mud-palette-error", 4.5)]
    [InlineData("mud-palette-warning-text", "mud-palette-warning", 4.5)]
    [InlineData("mud-palette-info-text", "mud-palette-info", 4.5)]
    [InlineData("mud-palette-lines-inputs", "mud-palette-background", 3.0)]
    [InlineData("mud-palette-lines-inputs", "mud-palette-surface", 3.0)]
    public async Task Served_theme_colours_meet_WCAG_AA_contrast(string foreground, string background, double minimum)
    {
        var served = await ServedAsync();

        var (pageR, pageG, pageB, _) = ParseColour(ThemeVariable(served.Html, "mud-palette-background"));
        var back = Over(ParseColour(ThemeVariable(served.Html, background)), (pageR, pageG, pageB));
        var fore = Over(ParseColour(ThemeVariable(served.Html, foreground)), back);
        var ratio = Contrast(fore, back);

        ratio.Should().BeGreaterThanOrEqualTo(minimum,
            $"--{foreground} on --{background} is {ratio:0.00}:1; WCAG AA asks {minimum}:1 "
            + "(4.5 for text, 3 for the boundary of a form control)");
    }

    // A translucent colour is what it looks like over whatever is behind it - MudBlazor's default
    // input border is white at 30%, which reads as a mid grey, not white.
    static (double R, double G, double B) Over((double R, double G, double B, double A) colour, (double R, double G, double B) behind) =>
        (colour.R * colour.A + behind.R * (1 - colour.A),
         colour.G * colour.A + behind.G * (1 - colour.A),
         colour.B * colour.A + behind.B * (1 - colour.A));

    static (double R, double G, double B, double A) ParseColour(string value)
    {
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            var alpha = hex.Length == 8 ? Channel(hex[6..8]) / 255 : 1;
            return (Channel(hex[0..2]), Channel(hex[2..4]), Channel(hex[4..6]), alpha);
        }

        var parts = Regex.Match(value, @"rgba?\((?<r>[\d.]+),\s*(?<g>[\d.]+),\s*(?<b>[\d.]+)(,\s*(?<a>[\d.]+))?");
        parts.Success.Should().BeTrue($"'{value}' should be a hex or rgb() colour");
        var a = parts.Groups["a"].Success ? Number(parts.Groups["a"].Value) : 1;
        return (Number(parts.Groups["r"].Value), Number(parts.Groups["g"].Value), Number(parts.Groups["b"].Value), a);

        static double Channel(string hex) => int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);
    }

    static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (lighter, darker) = (Luminance(a), Luminance(b)) is var (x, y) && x > y ? (x, y) : (y, x);
        return (lighter + 0.05) / (darker + 0.05);
    }

    static double Luminance((double R, double G, double B) colour) =>
        0.2126 * Linear(colour.R) + 0.7152 * Linear(colour.G) + 0.0722 * Linear(colour.B);

    static double Linear(double channel)
    {
        var c = channel / 255;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
