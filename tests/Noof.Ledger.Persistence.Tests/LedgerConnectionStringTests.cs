using AwesomeAssertions;

namespace Noof.Ledger.Persistence.Tests;

public class LedgerConnectionStringTests
{
    [Fact]
    public void Configuration_wins_over_every_fallback()
    {
        LedgerConnectionString.Resolve("Host=example;Database=configured")
            .Should().Be("Host=example;Database=configured");
    }

    [Fact]
    public void A_blank_configuration_value_is_treated_as_absent()
    {
        var act = () => LedgerConnectionString.Resolve("   ");

        act.Should().NotThrow("a blank setting must fall through to the credential file, not be used as one");
    }

    [Fact]
    public void The_resolved_string_names_the_ledger_database_not_postgres()
    {
        LedgerConnectionString.Resolve(null).Should().Contain("Database=noof_ledger")
            .And.NotContain("Database=postgres");
    }
}
