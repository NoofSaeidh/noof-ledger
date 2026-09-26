using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests.Diagnostics;

public class DatabaseHealthCheckTests
{
    static IDatabaseGate GateWith(DatabaseState state, string? detail = null)
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(state);
        gate.Detail.Returns(detail);
        return gate;
    }

    [Fact]
    public void Reports_its_name_order_and_log_category()
    {
        var check = new DatabaseHealthCheck(GateWith(DatabaseState.Ready));

        check.Name.Should().Be("Database");
        check.Order.Should().Be(10);
        check.LogCategory.Should().Be("Noof.Ledger.Host.Startup.DatabaseStartupService");
    }

    [Fact]
    public async Task Ready_is_healthy()
    {
        var check = new DatabaseHealthCheck(GateWith(DatabaseState.Ready));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Ok);
    }

    [Theory]
    [InlineData(DatabaseState.Waiting)]
    [InlineData(DatabaseState.Migrating)]
    public async Task Waiting_or_migrating_is_a_warning(DatabaseState state)
    {
        var check = new DatabaseHealthCheck(GateWith(state));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Warning);
        result.Summary.Should().Be("Offline — waiting for PostgreSQL");
    }

    [Fact]
    public async Task Failed_is_failing_and_carries_the_gates_detail()
    {
        var check = new DatabaseHealthCheck(GateWith(DatabaseState.Failed, "PostgresException: relation missing"));

        var result = await check.CheckAsync(TestContext.Current.CancellationToken);

        result.Level.Should().Be(HealthLevel.Failing);
        result.Summary.Should().Be("PostgresException: relation missing");
    }
}
