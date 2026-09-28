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

    // Without a TEMPLATE, CREATE DATABASE copies template1, and anything installed there reaches both
    // the per-run template and the fresh database MigratedTemplateTests compares it with - so the
    // comparison could not see it. template0 is the one database nobody can alter.
    [Fact]
    public void An_empty_test_database_is_copied_from_template0_not_the_installation_local_template1() =>
        DatabaseSettings.CreateEmptySql("noof_test_x").Should().Contain(" TEMPLATE template0 ");
}
