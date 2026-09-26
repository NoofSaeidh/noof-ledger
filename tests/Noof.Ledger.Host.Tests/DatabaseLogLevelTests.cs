using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Logging;
using Serilog.Events;

namespace Noof.Ledger.Host.Tests;

public class DatabaseLogLevelTests
{
    static (DatabaseLogLevel LogLevel, IDatabaseLogLevelStore Store, LogLevelSwitches Switches) Build()
    {
        var store = Substitute.For<IDatabaseLogLevelStore>();
        var services = new ServiceCollection();
        services.AddSingleton(store);
        var provider = services.BuildServiceProvider();
        var switches = new LogLevelSwitches();
        var logLevel = new DatabaseLogLevel(switches, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatabaseLogLevel>.Instance);
        return (logLevel, store, switches);
    }

    [Fact]
    public async Task SetAsync_saves_then_applies_and_Current_becomes_the_new_level()
    {
        var (logLevel, store, switches) = Build();

        await logLevel.SetAsync(LogSeverity.Debug, TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            store.SaveAsync(LogSeverity.Debug, Arg.Any<CancellationToken>());
        });
        await store.Received(1).SaveAsync(LogSeverity.Debug, Arg.Any<CancellationToken>());
        switches.Database.MinimumLevel.Should().Be(LogEventLevel.Debug);
        logLevel.Current.Should().Be(LogSeverity.Debug);
    }

    [Fact]
    public async Task SetAsync_with_a_level_outside_Choices_throws_and_saves_nothing()
    {
        var (logLevel, store, _) = Build();

        var act = () => logLevel.SetAsync(LogSeverity.Warning, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await store.DidNotReceive().SaveAsync(Arg.Any<LogSeverity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_store_that_throws_on_save_leaves_Current_and_the_switch_unchanged()
    {
        var (logLevel, store, switches) = Build();
        store.SaveAsync(Arg.Any<LogSeverity>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unreachable"));
        var before = logLevel.Current;

        var act = () => logLevel.SetAsync(LogSeverity.Debug, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        logLevel.Current.Should().Be(before);
        switches.Database.MinimumLevel.Should().Be(LogEventLevel.Information);
    }

    [Fact]
    public async Task LoadAsync_applies_a_stored_Debug()
    {
        var (logLevel, store, switches) = Build();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(LogSeverity.Debug);

        await logLevel.LoadAsync(TestContext.Current.CancellationToken);

        switches.Database.MinimumLevel.Should().Be(LogEventLevel.Debug);
        logLevel.Current.Should().Be(LogSeverity.Debug);
    }

    [Fact]
    public async Task LoadAsync_ignores_a_null_stored_value()
    {
        var (logLevel, store, switches) = Build();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns((LogSeverity?)null);

        await logLevel.LoadAsync(TestContext.Current.CancellationToken);

        switches.Database.MinimumLevel.Should().Be(LogEventLevel.Information);
    }

    [Fact]
    public async Task LoadAsync_ignores_a_stored_Warning_because_it_is_not_a_choice()
    {
        var (logLevel, store, switches) = Build();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(LogSeverity.Warning);

        await logLevel.LoadAsync(TestContext.Current.CancellationToken);

        switches.Database.MinimumLevel.Should().Be(LogEventLevel.Information);
    }

    [Theory]
    [InlineData(LogSeverity.Verbose, LogEventLevel.Verbose)]
    [InlineData(LogSeverity.Debug, LogEventLevel.Debug)]
    [InlineData(LogSeverity.Information, LogEventLevel.Information)]
    [InlineData(LogSeverity.Warning, LogEventLevel.Warning)]
    [InlineData(LogSeverity.Error, LogEventLevel.Error)]
    [InlineData(LogSeverity.Fatal, LogEventLevel.Fatal)]
    public void LogSeverity_casts_to_the_matching_LogEventLevel(LogSeverity severity, LogEventLevel expected)
    {
        ((LogEventLevel)severity).Should().Be(expected);
        severity.ToString().Should().Be(expected.ToString());
    }

    [Fact]
    public void Choices_is_Verbose_Debug_Information()
    {
        var (logLevel, _, _) = Build();

        ((IDatabaseLogLevel)logLevel).Choices.Should().Equal(LogSeverity.Verbose, LogSeverity.Debug, LogSeverity.Information);
    }
}
