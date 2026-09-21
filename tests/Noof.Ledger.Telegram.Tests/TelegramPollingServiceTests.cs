using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Telegram;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramPollingServiceTests
{
    static IServiceScopeFactory ScopeFactoryFor(ISecretStore secretStore, ITelegramUpdateRouter? router = null)
    {
        var offsetStore = new TelegramUpdateOffsetStore(secretStore);

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(ISecretStore)).Returns(secretStore);
        provider.GetService(typeof(TelegramUpdateOffsetStore)).Returns(offsetStore);
        if (router is not null)
            provider.GetService(typeof(ITelegramUpdateRouter)).Returns(router);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    static ISecretStore NoTokenYet()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramBotToken, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));
        return secretStore;
    }

    static ISecretStore WithToken(string token)
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramBotToken, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, token));
        secretStore.GetAsync(TelegramUpdateOffsetStore.Key, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));
        return secretStore;
    }

    static TelegramPollingService CreateService(
        ISecretStore secretStore, ITelegramBotClientFactory clientFactory, TelegramClientHandle handle, ITelegramUpdateRouter? router = null) =>
        new(
            ScopeFactoryFor(secretStore, router),
            clientFactory,
            handle,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            NullLogger<TelegramPollingService>.Instance);

    [Fact]
    public async Task Stays_idle_when_no_token_has_been_saved_yet()
    {
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        var service = CreateService(NoTokenYet(), clientFactory, new TelegramClientHandle());

        var result = await service.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Idle);
        clientFactory.DidNotReceive().Create(Arg.Any<string>());
    }

    [Fact]
    public async Task Builds_the_client_and_deletes_the_webhook_the_first_time_a_token_appears()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var handle = new TelegramClientHandle();

        var result = await CreateService(WithToken("tok1"), clientFactory, handle).RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Processed);
        handle.Current.Should().BeSameAs(client);
        await client.Received(1).SendRequest(Arg.Any<DeleteWebhookRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_rebuild_the_client_when_the_token_is_unchanged()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var service = CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle());

        await service.RunTickAsync(TestContext.Current.CancellationToken);
        await service.RunTickAsync(TestContext.Current.CancellationToken);

        clientFactory.Received(1).Create("tok1");
        await client.Received(1).SendRequest(Arg.Any<DeleteWebhookRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rebuilds_the_client_and_redeletes_the_webhook_when_the_token_changes()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramBotToken, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, "tok1"), new SecretResult(SecretState.Present, "tok2"));
        secretStore.GetAsync(TelegramUpdateOffsetStore.Key, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));

        var client1 = Substitute.For<ITelegramBotClient>();
        client1.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
        var client2 = Substitute.For<ITelegramBotClient>();
        client2.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client1);
        clientFactory.Create("tok2").Returns(client2);
        var handle = new TelegramClientHandle();
        var service = CreateService(secretStore, clientFactory, handle);

        await service.RunTickAsync(TestContext.Current.CancellationToken);
        await service.RunTickAsync(TestContext.Current.CancellationToken);

        handle.Current.Should().BeSameAs(client2);
        await client1.Received(1).SendRequest(Arg.Any<DeleteWebhookRequest>(), Arg.Any<CancellationToken>());
        await client2.Received(1).SendRequest(Arg.Any<DeleteWebhookRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Routes_each_update_and_persists_the_offset_after_each_one()
    {
        var secretStore = WithToken("tok1");
        var update10 = new Update { Id = 10, Message = new Message { Id = 1, Chat = new Chat { Id = 111L }, Text = "a" } };
        var update11 = new Update { Id = 11, Message = new Message { Id = 2, Chat = new Chat { Id = 111L }, Text = "b" } };
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns([update10, update11]);
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var router = Substitute.For<ITelegramUpdateRouter>();

        var result = await CreateService(secretStore, clientFactory, new TelegramClientHandle(), router)
            .RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Processed);
        Received.InOrder(() =>
        {
            router.HandleAsync(update10, "Europe/Belgrade", Arg.Any<CancellationToken>());
            secretStore.SetAsync(TelegramUpdateOffsetStore.Key, "11", Arg.Any<CancellationToken>());
            router.HandleAsync(update11, "Europe/Belgrade", Arg.Any<CancellationToken>());
            secretStore.SetAsync(TelegramUpdateOffsetStore.Key, "12", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task GetUpdates_throwing_is_reported_as_failed_without_throwing_or_advancing_the_offset()
    {
        var secretStore = WithToken("tok1");
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
            .Returns<Update[]>(_ => throw new HttpRequestException("cable pulled"));
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var router = Substitute.For<ITelegramUpdateRouter>();

        var result = await CreateService(secretStore, clientFactory, new TelegramClientHandle(), router)
            .RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Failed);
        await router.DidNotReceive().HandleAsync(Arg.Any<Update>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await secretStore.DidNotReceive().SetAsync(TelegramUpdateOffsetStore.Key, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Keeps_polling_after_a_failed_tick()
    {
        var secretStore = WithToken("tok1");
        var client = Substitute.For<ITelegramBotClient>();
        var attempt = 0;
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempt++;
                if (attempt == 1)
                    throw new HttpRequestException("cable pulled");
                return Array.Empty<Update>();
            });
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var service = CreateService(secretStore, clientFactory, new TelegramClientHandle());

        var first = await service.RunTickAsync(TestContext.Current.CancellationToken);
        var second = await service.RunTickAsync(TestContext.Current.CancellationToken);

        first.Should().Be(TelegramPollResult.Failed);
        second.Should().Be(TelegramPollResult.Processed);
        await client.Received(2).SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>());
    }

    // Commit a069aae moved the token fetch inside the try but left scope creation and the
    // ISecretStore resolution outside it. An exception thrown while resolving a dependency is
    // exactly as fatal to the host as one thrown by the token fetch itself - BackgroundService's
    // default ExceptionBehavior is StopHost - so this must be swallowed and reported as Failed
    // the same way. A mock configured to throw would prove the same thing less directly than a
    // fake that actually behaves like a broken container.
    [Fact]
    public async Task A_DI_resolution_failure_while_creating_the_scope_is_reported_as_failed_without_throwing()
    {
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        var service = new TelegramPollingService(
            new ThrowingScopeFactory(),
            clientFactory,
            new TelegramClientHandle(),
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            NullLogger<TelegramPollingService>.Instance);

        var result = await service.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Failed);
        clientFactory.DidNotReceive().Create(Arg.Any<string>());
    }

    sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new ThrowingScope();

        sealed class ThrowingScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new ThrowingProvider();

            public void Dispose() { }
        }

        sealed class ThrowingProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(ISecretStore)
                    ? throw new InvalidOperationException("the container cannot resolve ISecretStore")
                    : null;
        }
    }
}
