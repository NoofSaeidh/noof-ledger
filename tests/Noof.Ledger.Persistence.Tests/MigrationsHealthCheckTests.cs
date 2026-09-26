using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
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
    public async Task Reports_its_name_order_and_log_category()
    {
        await using var db = await fixture.CreateContextAsync();
        var check = new MigrationsHealthCheck(db, GateWith(DatabaseState.Ready));

        check.Name.Should().Be("Migrations");
        check.Order.Should().Be(20);
        check.LogCategory.Should().Be(DbLoggerCategory.Migrations.Name);
    }

    [Fact]
    public async Task Reports_a_warning_without_touching_the_database_when_the_gate_is_not_ready()
    {
        await using var db = await fixture.CreateContextAsync();
        var check = new MigrationsHealthCheck(db, GateWith(DatabaseState.Waiting));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
        result.Summary.Should().Be("Waiting for the database");
    }

    [Fact]
    public async Task A_fully_migrated_clone_reports_ok()
    {
        await using var connection = await fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(connection).Options;
        await using var db = new LedgerDbContext(options);
        var check = new MigrationsHealthCheck(db, GateWith(DatabaseState.Ready));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Ok);
    }

    [Fact]
    public async Task An_unmigrated_database_reports_failing_naming_the_pending_count()
    {
        await using var db = await fixture.CreateContextAsync();
        var check = new MigrationsHealthCheck(db, GateWith(DatabaseState.Ready));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Failing);
        result.Summary.Should().MatchRegex(@"^\d+ migration\(s\) pending$");
    }
}
