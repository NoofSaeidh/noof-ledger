using AwesomeAssertions;

namespace Noof.Ledger.Persistence.Tests;

// One test below rewrites NOOF_TEST_PG for the whole process, and every database test reads that
// variable to find PostgreSQL. Run in parallel with them, it sent whichever test opened a connection
// inside that window to "Host=from-the-env-var" - "No such host is known", in a test about wallets.
[CollectionDefinition(nameof(ProcessEnvironmentCollection), DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

[Collection(nameof(ProcessEnvironmentCollection))]
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

    [Fact]
    public void The_test_suites_environment_variable_cannot_steer_the_application_at_a_database()
    {
        // NOOF_TEST_PG is set machine-wide by ops/reset-database-auth.ps1 and names the postgres
        // database. Resolve used to read it and rewrite the name to noof_ledger, so launching the
        // published host with no configured connection string silently attached the application to
        // the operator's real ledger. It happened once, during Phase 1B, and cost nothing only
        // because that database was still empty.
        var restore = Environment.GetEnvironmentVariable("NOOF_TEST_PG");
        Environment.SetEnvironmentVariable("NOOF_TEST_PG", "Host=from-the-env-var;Database=postgres;Username=none");

        try
        {
            var act = () => LedgerConnectionString.Resolve(null, "database_that_does_not_exist");

            // Either it falls through to the credential file, or there is no credential file and it
            // throws - but the environment variable must never be what decides.
            if (File.Exists(LedgerConnectionString.CredentialFile))
                act().Should().NotContain("from-the-env-var");
            else
                act.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOOF_TEST_PG", restore);
        }
    }
}
