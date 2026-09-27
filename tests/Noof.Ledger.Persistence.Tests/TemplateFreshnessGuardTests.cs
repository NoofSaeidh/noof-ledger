using AwesomeAssertions;

namespace Noof.Ledger.Persistence.Tests;

public class TemplateFreshnessGuardTests
{
    [Fact]
    public void Passes_silently_when_the_template_already_has_the_latest_migration()
    {
        var act = () => TemplateFreshnessGuard.EnsureCurrent(
            latestInAssembly: "20260927153746_AddReceiptCaptureSourceXor",
            latestAppliedToTemplate: "20260927153746_AddReceiptCaptureSourceXor");

        act.Should().NotThrow();
    }

    [Fact]
    public void Fails_fast_naming_the_update_command_when_the_template_is_behind()
    {
        var act = () => TemplateFreshnessGuard.EnsureCurrent(
            latestInAssembly: "20260927153746_AddReceiptCaptureSourceXor",
            latestAppliedToTemplate: "20260926010815_AddAppSetting");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*run.ps1 update-test-template*");
    }

    [Fact]
    public void Fails_fast_when_the_template_has_never_been_migrated()
    {
        var act = () => TemplateFreshnessGuard.EnsureCurrent(
            latestInAssembly: "20260927153746_AddReceiptCaptureSourceXor",
            latestAppliedToTemplate: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*run.ps1 update-test-template*");
    }
}
