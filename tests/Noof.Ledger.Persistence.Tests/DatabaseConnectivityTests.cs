using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class DatabaseConnectivityTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Opens_and_closes_a_connection_against_a_reachable_database()
    {
        var connectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();
        await using var services = new ServiceCollection()
            .AddDbContext<LedgerDbContext>(options => options.UseNpgsql(connectionString))
            .BuildServiceProvider();

        var act = () => services.OpenNoofDatabaseConnectionAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Throws_against_an_unreachable_database()
    {
        await using var services = new ServiceCollection()
            .AddDbContext<LedgerDbContext>(options => options.UseNpgsql(
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2"))
            .BuildServiceProvider();

        var act = () => services.OpenNoofDatabaseConnectionAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<NpgsqlException>();
    }
}
