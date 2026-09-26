using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Noof.Ledger.E2E.Tests;

public sealed partial class ContentRootIndependenceTests(DifferentWorkingDirectoryHostFixture fixture)
    : IClassFixture<DifferentWorkingDirectoryHostFixture>
{
    [Fact]
    public async Task Static_assets_are_served_with_a_non_empty_body_when_launched_from_another_directory()
    {
        using var client = new HttpClient();

        var loginResponse = await client.GetAsync(fixture.BaseUrl + "/account/login", TestContext.Current.CancellationToken);
        loginResponse.EnsureSuccessStatusCode();
        var html = await loginResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        html.Should().Contain("_framework/blazor.web.js");

        var match = AppCssHrefPattern().Match(html);
        match.Success.Should().BeTrue("the sign-in page must reference its own (possibly fingerprinted) stylesheet");

        var cssResponse = await client.GetAsync(
            fixture.BaseUrl + "/" + match.Groups[1].Value, TestContext.Current.CancellationToken);
        cssResponse.EnsureSuccessStatusCode();

        var cssBytes = await cssResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        cssBytes.Should().NotBeEmpty(
            "a wrong content root answers every static asset 200 with an empty body instead of 404");
    }

    [GeneratedRegex(@"href=""(_content/Noof\.Ledger\.Web/app[^""]*\.css)""")]
    private static partial Regex AppCssHrefPattern();
}
