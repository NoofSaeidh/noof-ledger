using AwesomeAssertions;
using Noof.Ledger.Persistence.Backup;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

public class PgDumpArgumentsTests
{
    [Fact]
    public void Names_host_port_user_database_and_the_target_file_but_never_the_password()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            "Host=db.example.internal;Port=5433;Database=noof_ledger;Username=noof;Password=correct-horse-battery-staple");

        var args = PgDumpArguments.Build(builder, @"C:\backups\out.dump");

        args.Should().ContainInOrder("-h", "db.example.internal", "-p", "5433", "-U", "noof", "-d", "noof_ledger", "-f", @"C:\backups\out.dump");
        args.Should().Contain("-Fc");
        args.Should().Contain("--no-password");
        args.Should().NotContain(a => a.Contains("correct-horse-battery-staple", StringComparison.Ordinal));
    }
}
