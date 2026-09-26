using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Logging;

namespace Noof.Ledger.Host.Tests;

public class DatabaseLogLevelLoaderTests
{
    static DatabaseLogLevel LogLevelFor(IDatabaseLogLevelStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        var provider = services.BuildServiceProvider();
        return new DatabaseLogLevel(new LogLevelSwitches(), provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatabaseLogLevel>.Instance);
    }

    [Fact]
    public async Task With_a_never_Ready_gate_the_store_is_never_called()
    {
        var store = Substitute.For<IDatabaseLogLevelStore>();
        var logLevel = LogLevelFor(store);
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource().Task);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var readySignal = new DatabaseLogLevelReadySignal();
        var loader = new DatabaseLogLevelLoader(gate, logLevel, readySignal, time, NullLogger<DatabaseLogLevelLoader>.Instance);

        await loader.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await store.DidNotReceive().GetAsync(Arg.Any<CancellationToken>());
            readySignal.WaitForLoadAsync(TestContext.Current.CancellationToken).IsCompleted.Should().BeFalse();
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await loader.StopAsync(stopTimeout.Token);
        }
    }

    [Fact]
    public async Task A_failing_load_retries_after_the_delay_and_then_applies_the_value()
    {
        var store = Substitute.For<IDatabaseLogLevelStore>();
        store.GetAsync(Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new InvalidOperationException("database unreachable"),
                _ => (DatabaseLogLevelSetting?)DatabaseLogLevelSetting.For(LogSeverity.Debug));
        var logLevel = LogLevelFor(store);
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var readySignal = new DatabaseLogLevelReadySignal();
        var loader = new DatabaseLogLevelLoader(gate, logLevel, readySignal, time, NullLogger<DatabaseLogLevelLoader>.Instance);

        await loader.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await store.Received(1).GetAsync(Arg.Any<CancellationToken>());
            logLevel.Current.Should().Be(LogSeverity.Information);
            readySignal.WaitForLoadAsync(TestContext.Current.CancellationToken).IsCompleted.Should().BeFalse(
                "the first attempt failed, so nothing has been applied yet");

            time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await store.Received(2).GetAsync(Arg.Any<CancellationToken>());
            logLevel.Current.Should().Be(LogSeverity.Debug);
            readySignal.WaitForLoadAsync(TestContext.Current.CancellationToken).IsCompletedSuccessfully.Should().BeTrue(
                "the second attempt succeeded, so the signal must now be set");

            loader.ExecuteTask!.IsFaulted.Should().BeFalse();
        }
        finally
        {
            await loader.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
