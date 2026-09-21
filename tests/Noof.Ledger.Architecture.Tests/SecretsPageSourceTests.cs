using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class SecretsPageSourceTests
{
    static string SourceText() => File.ReadAllText(Path.Combine(
        RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", "Pages", "Settings", "Secrets.razor"));

    [Fact]
    public void Reads_status_only_and_never_the_plaintext_secret()
    {
        var source = SourceText();

        source.Should().Contain("GetStatusAsync(",
            "the settings page must show set/not-set status");
        source.Should().NotContain("GetAsync(",
            "ISecretStore.GetAsync hands back a plaintext secret - the settings page has no business holding one");
    }

    [Fact]
    public void Disables_prerendering_so_a_typed_secret_never_reaches_served_HTML()
    {
        SourceText().Should().Contain("prerender: false");
    }

    [Fact]
    public void Secret_inputs_opt_out_of_browser_autocomplete()
    {
        SourceText().Should().Contain("autocomplete=\"off\"");
    }
}
