using AwesomeAssertions;

namespace Noof.Ledger.Persistence.Tests;

public class TemplateFreshnessGuardTests
{
    static readonly string[] SomeMigrations =
    [
        "20260921071450_AddAppSecret",
        "20260926010815_AddAppSetting",
        "20260927153746_AddReceiptCaptureSourceXor",
    ];

    [Fact]
    public void Passes_silently_when_the_template_has_exactly_the_assemblys_migrations_in_order()
    {
        var act = () => TemplateFreshnessGuard.EnsureCurrent(
            migrationsInAssembly: SomeMigrations,
            migrationsAppliedToTemplate: SomeMigrations);

        act.Should().NotThrow();
    }

    [Fact]
    public void Fails_fast_naming_the_update_command_when_the_template_is_behind()
    {
        var act = () => TemplateFreshnessGuard.EnsureCurrent(
            migrationsInAssembly: SomeMigrations,
            migrationsAppliedToTemplate: SomeMigrations[..2]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*run.ps1 update-test-template*");
    }

    [Fact]
    public void Fails_fast_when_the_template_has_never_been_migrated()
    {
        var act = () => TemplateFreshnessGuard.EnsureCurrent(
            migrationsInAssembly: SomeMigrations,
            migrationsAppliedToTemplate: []);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*run.ps1 update-test-template*");
    }

    [Fact]
    public void Fails_fast_when_the_histories_diverge_in_the_middle_even_though_the_latest_id_matches()
    {
        // A rebase, or a migration reverted and re-added under a different name in another worktree,
        // can leave a template whose LATEST applied migration happens to equal the assembly's latest
        // while an earlier one differs - comparing only the last id would wrongly call this current.
        string[] divergedButSameLatest =
        [
            "20260921071450_AddAppSecret",
            "20260925999999_SomeOtherWorktreesMigration",
            "20260927153746_AddReceiptCaptureSourceXor",
        ];

        var act = () => TemplateFreshnessGuard.EnsureCurrent(
            migrationsInAssembly: SomeMigrations,
            migrationsAppliedToTemplate: divergedButSameLatest);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*run.ps1 update-test-template*");
    }
}
