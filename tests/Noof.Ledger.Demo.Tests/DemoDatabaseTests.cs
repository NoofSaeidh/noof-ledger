using AwesomeAssertions;
using Npgsql;

namespace Noof.Ledger.Demo.Tests;

public sealed class DemoDatabaseTests
{
    const string Admin = "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=not-used";

    [Fact]
    public void The_demo_connection_string_names_exactly_the_demo_database()
    {
        var connection = new NpgsqlConnectionStringBuilder(DemoDatabase.For(Admin, DemoDatabase.Name));

        connection.Database.Should().Be("noof_ledger_demo");
        connection.Host.Should().Be("127.0.0.1");
    }

    [Theory]
    [InlineData("noof_ledger")]
    [InlineData("noof_ledger_test_template")]
    [InlineData("postgres")]
    [InlineData("noof_ledger_demo_old")]
    [InlineData("noof_test_")]
    [InlineData("noof_test_x\"; DROP DATABASE noof_ledger; --")]
    public void Any_other_database_is_refused(string database)
    {
        var act = () => DemoDatabase.For(Admin, database);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{database}*");
    }

    [Fact]
    public void A_throwaway_test_database_is_allowed()
    {
        new NpgsqlConnectionStringBuilder(DemoDatabase.For(Admin, "noof_test_0123abcd"))
            .Database.Should().Be("noof_test_0123abcd");
    }

    [Fact]
    public void Missing_credential_file_names_the_command_that_creates_it()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"noof-missing-{Guid.NewGuid():N}", "db.connection");

        var act = () => DemoDatabase.AdminConnectionString(missing);

        act.Should().Throw<InvalidOperationException>().WithMessage("*run.ps1 db-auth-reset*");
    }

    [Fact]
    public void The_credential_file_is_read_and_trimmed()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("noof-demo-cred-").FullName, "db.connection");
        File.WriteAllText(file, Admin + "\r\n");

        DemoDatabase.AdminConnectionString(file).Should().Be(Admin);
    }
}
