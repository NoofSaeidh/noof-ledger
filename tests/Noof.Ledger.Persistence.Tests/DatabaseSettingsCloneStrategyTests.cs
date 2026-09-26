using AwesomeAssertions;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Persistence.Tests;

// PostgreSQL records no strategy once a database exists, so the SQL text is the only cheap detector.
// Why FILE_COPY, with the measurements, is at the top of DatabaseSettings.
public class DatabaseSettingsCloneStrategyTests
{
    [Fact]
    public void Cloning_the_test_template_names_the_file_copy_strategy() =>
        DatabaseSettings.CreateFromTemplateSql("noof_test_x").Should().EndWith(" STRATEGY FILE_COPY");

    [Fact]
    public void Creating_an_empty_test_database_names_the_file_copy_strategy() =>
        DatabaseSettings.CreateEmptySql("noof_test_x").Should().EndWith(" STRATEGY FILE_COPY");
}
