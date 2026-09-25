using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace Noof.Ledger.Persistence.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public class DesignTimeDbContextFactoryTests
{
    [Fact]
    public void The_override_variable_wins_over_the_resolved_connection_string()
    {
        var restore = Environment.GetEnvironmentVariable(DesignTimeDbContextFactory.ConnectionOverrideVariable);
        Environment.SetEnvironmentVariable(
            DesignTimeDbContextFactory.ConnectionOverrideVariable,
            "Host=from-the-override;Database=noof_ledger_test_template;Username=someone");

        try
        {
            using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

            context.Database.GetConnectionString().Should().Contain("from-the-override",
                "run.ps1's update-test-template hands the template connection string through this " +
                "variable, never as a --connection command-line argument, so the password never " +
                "appears in a process's argument list");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesignTimeDbContextFactory.ConnectionOverrideVariable, restore);
        }
    }

    [Fact]
    public void A_blank_override_falls_back_to_the_resolved_connection_string()
    {
        var restore = Environment.GetEnvironmentVariable(DesignTimeDbContextFactory.ConnectionOverrideVariable);
        Environment.SetEnvironmentVariable(DesignTimeDbContextFactory.ConnectionOverrideVariable, "   ");

        try
        {
            using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

            context.Database.GetConnectionString().Should().Contain("Database=noof_ledger")
                .And.NotContain("from-the-override");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesignTimeDbContextFactory.ConnectionOverrideVariable, restore);
        }
    }
}
