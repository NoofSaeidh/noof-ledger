using AwesomeAssertions;
using Npgsql;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Persistence.Tests;

public class DatabaseReachableTests
{
    [Fact]
    public async Task Server_is_reachable_and_is_postgres_18_or_later()
    {
        await using var connection = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        connection.PostgreSqlVersion.Major.Should().BeGreaterThanOrEqualTo(18);
    }

    [Fact]
    public async Task Unreachable_server_fails_with_a_socket_diagnostic_not_a_hang()
    {
        var unreachable = DatabaseSettings.AdminConnectionString
            .Replace("Port=5432", "Port=5433", StringComparison.Ordinal) + ";Timeout=2";

        await using var connection = new NpgsqlConnection(unreachable);

        var act = async () => await connection.OpenAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<NpgsqlException>();
    }
}
