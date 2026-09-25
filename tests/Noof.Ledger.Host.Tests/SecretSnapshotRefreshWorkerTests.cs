using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests;

public class SecretSnapshotRefreshWorkerTests
{
    static IServiceScopeFactory ScopeFactoryFor(ISecretStore store)
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(ISecretStore)).Returns(store);
        provider.GetService(typeof(IEnumerable<ISecretProbe>)).Returns(Array.Empty<ISecretProbe>());
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    static ISecretStore StoreWith(string key, string value)
    {
        var store = Substitute.For<ISecretStore>();
        store.GetAsync(key, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, value));
        store.GetAsync(Arg.Is<string>(k => k != key), Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));
        return store;
    }

    [Fact]
    public void Values_shorter_than_eight_characters_are_ignored()
    {
        var snapshot = new SecretSnapshot(ScopeFactoryFor(StoreWith(SecretKeys.TelegramBotToken, "short")), "db-password-long-enough");

        snapshot.CurrentValues.Should().NotContain("short").And.Contain("db-password-long-enough");
    }

    [Fact]
    public async Task RefreshAsync_picks_up_a_secret_present_at_refresh_time()
    {
        var store = StoreWith(SecretKeys.TelegramBotToken, "a-long-enough-bot-token");
        var snapshot = new SecretSnapshot(ScopeFactoryFor(store), "db-password-long-enough");

        await snapshot.RefreshAsync(TestContext.Current.CancellationToken);

        snapshot.CurrentValues.Should().Contain("a-long-enough-bot-token");
    }

    [Fact]
    public async Task The_worker_refreshes_once_the_gate_is_ready_then_every_five_minutes()
    {
        var store = StoreWith(SecretKeys.TelegramBotToken, "a-long-enough-bot-token");
        var snapshot = new SecretSnapshot(ScopeFactoryFor(store), "db-password-long-enough");
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
        var worker = new SecretSnapshotRefreshWorker(gate, snapshot, time);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await store.Received(1).GetAsync(SecretKeys.TelegramBotToken, Arg.Any<CancellationToken>());

            time.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            await store.Received(2).GetAsync(SecretKeys.TelegramBotToken, Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
