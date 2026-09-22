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

    [Fact]
    public void Test_button_calls_the_probe_port_not_the_secret_store()
    {
        var source = SourceText();

        source.Should().Contain("ProbeAsync(",
            "the Test button must go through ISecretProbe, which structurally cannot hand back a plaintext secret");
        source.Should().NotContain("GetAsync(",
            "re-asserted here: wiring up the probe must not introduce a second path back to the plaintext secret");
    }

    [Fact]
    public void The_in_flight_testing_flag_is_cleared_in_a_finally_block()
    {
        // AnthropicKeyProbe.ProbeAsync is exception-total and should never throw, but TestAsync
        // must not depend on that alone: whatever the probe does, row.Testing must go back to
        // false, or a surprise from a future probe implementation leaves the button permanently
        // disabled until the page is reloaded.
        var source = SourceText();

        source.Should().Contain("finally",
            "row.Testing must be reset in a finally block so it clears even if ProbeAsync somehow throws");
    }

    [Fact]
    public void Disposes_a_component_owned_cancellation_source_so_navigating_away_cancels_in_flight_calls()
    {
        var source = SourceText();

        source.Should().Contain("@implements IDisposable",
            "Blazor gives a component no CancellationToken of its own - navigating away must cancel an " +
            "in-flight save, status read or probe via a token this component owns and disposes");
        source.Should().Contain("CancellationTokenSource",
            "the component must own a CancellationTokenSource to cancel on dispose");
        source.Should().NotContain("CancellationToken.None",
            "CancellationToken.None never cancels - navigating away mid-save or mid-probe would let the " +
            "call run to completion and then write into a disposed component");
    }
}
