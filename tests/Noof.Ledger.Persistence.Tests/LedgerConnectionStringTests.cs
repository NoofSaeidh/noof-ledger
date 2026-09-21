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

    [Fact]
    public void Error_detail_is_stripped_so_parameter_values_cannot_reach_an_exception_message()
    {
        var resolved = LedgerConnectionString.Resolve(
            "Host=example;Database=noof_ledger;Include Error Detail=true");

        resolved.Should().NotContain("Include Error Detail",
            "Npgsql puts parameter values into exception text when this is on, and Phase 1 flows secrets through EF");
    }

    [Fact]
    public void A_connection_string_without_error_detail_is_returned_unchanged()
    {
        const string plain = "Host=example;Database=noof_ledger;Username=someone";

        LedgerConnectionString.Resolve(plain).Should().Be(plain);
    }
}
