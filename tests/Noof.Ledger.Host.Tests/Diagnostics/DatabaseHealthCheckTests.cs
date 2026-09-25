using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
    public async Task Ready_is_healthy()
    {
        var check = new DatabaseHealthCheck(GateWith(DatabaseState.Ready));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Theory]
    [InlineData(DatabaseState.Waiting)]
    [InlineData(DatabaseState.Migrating)]
    public async Task Waiting_or_migrating_is_degraded(DatabaseState state)
    {
        var check = new DatabaseHealthCheck(GateWith(state));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Offline — waiting for PostgreSQL");
    }

    [Fact]
    public async Task Failed_is_unhealthy_and_carries_the_gates_detail()
    {
        var check = new DatabaseHealthCheck(GateWith(DatabaseState.Failed, "PostgresException: relation missing"));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("PostgresException: relation missing");
    }
}
