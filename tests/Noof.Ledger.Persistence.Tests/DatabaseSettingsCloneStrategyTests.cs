using AwesomeAssertions;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Persistence.Tests;

// Measured on the shared test server (Phase 6): with 40 live clones, one CHECKPOINT took 42.6s under
// the default WAL_LOG strategy and 0.17s under FILE_COPY. Every DROP DATABASE waits for a checkpoint,
// so WAL_LOG clones are what made full-suite drops outlast their 120s budget.
public class DatabaseSettingsCloneStrategyTests
{
    [Fact]
    public void A_clone_of_the_test_template_is_created_with_the_file_copy_strategy() =>
        DatabaseSettings.CreateFromTemplateSql("noof_test_x").Should().EndWith(" STRATEGY FILE_COPY");

    [Fact]
    public void An_empty_test_database_is_created_with_the_file_copy_strategy() =>
        DatabaseSettings.CreateEmptySql("noof_test_x").Should().EndWith(" STRATEGY FILE_COPY");
}
