using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class MigrationsHealthCheckTests(PostgresFixture fixture)
{
    static IDatabaseGate GateWith(DatabaseState state)
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(state);
        return gate;
    }

    [Fact]
    public async Task Reports_degraded_without_touching_the_database_when_the_gate_is_not_ready()
    {
        await using var db = await fixture.CreateContextAsync();
        var check = new MigrationsHealthCheck(db, GateWith(DatabaseState.Waiting));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Waiting for the database");
    }

    [Fact]
    public async Task A_fully_migrated_clone_reports_healthy()
    {
        await using var connection = await fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(connection).Options;
        await using var db = new LedgerDbContext(options);
        var check = new MigrationsHealthCheck(db, GateWith(DatabaseState.Ready));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task An_unmigrated_database_reports_unhealthy_naming_the_pending_count()
    {
        await using var db = await fixture.CreateContextAsync();
        var check = new MigrationsHealthCheck(db, GateWith(DatabaseState.Ready));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().MatchRegex(@"^\d+ migration\(s\) pending$");
    }
}
