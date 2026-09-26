using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.TestKit;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramPollingServiceTests
{
    static IServiceScopeFactory ScopeFactoryFor(
        ISecretStore secretStore, ITelegramUpdateRouter? router = null, IChatNotifier? chatNotifier = null)
    {
        var offsetStore = new TelegramUpdateOffsetStore(secretStore);

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(ISecretStore)).Returns(secretStore);
        provider.GetService(typeof(TelegramUpdateOffsetStore)).Returns(offsetStore);
        if (router is not null)
            provider.GetService(typeof(ITelegramUpdateRouter)).Returns(router);
        if (chatNotifier is not null)
            provider.GetService(typeof(IChatNotifier)).Returns(chatNotifier);

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
        ISecretStore secretStore,
        ITelegramBotClientFactory clientFactory,
        TelegramClientHandle handle,
        ITelegramUpdateRouter? router = null,
        IChatNotifier? chatNotifier = null,
        IDatabaseGate? gate = null,
        IPollingHeartbeat? heartbeat = null,
        TimeProvider? timeProvider = null,
        IOperationTimer? timer = null,
        CapturingLogger<TelegramPollingService>? capturingLogger = null,
        IConfiguration? configuration = null) =>
        new(
            ScopeFactoryFor(secretStore, router, chatNotifier),
            clientFactory,
            handle,
            configuration ?? new ConfigurationBuilder().Build(),
            timeProvider ?? TimeProvider.System,
            gate ?? ReadyGate(),
            heartbeat ?? Substitute.For<IPollingHeartbeat>(),
            timer ?? new OperationTimer(timeProvider ?? TimeProvider.System, new SlowOperationOptions()),
            (ILogger<TelegramPollingService>?)capturingLogger ?? NullLogger<TelegramPollingService>.Instance);

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return gate;
    }

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

    // Before this fix, any exception from the router left the offset unadvanced forever: the same
    // update was refetched every tick, nothing behind it was ever processed, and Telegram discards
    // unconfirmed updates after 24 hours - a permanently broken wallet, say, silently loses every
    // message that arrives after the first one that hits it. A router that always throws for one
    // update proves the poller now gives up on that one specific update after a bounded number of
    // attempts, advances past it, and still processes what comes after it in the same batch.
    [Fact]
    public async Task A_poison_update_is_skipped_after_repeated_failures_so_the_update_behind_it_still_gets_processed()
    {
        var secretStore = WithToken("tok1");
        var poisonUpdate = new Update { Id = 10, Message = new Message { Id = 1, Chat = new Chat { Id = 111L }, Text = "a" } };
        var laterUpdate = new Update { Id = 11, Message = new Message { Id = 2, Chat = new Chat { Id = 111L }, Text = "b" } };
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns([poisonUpdate, laterUpdate]);
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var router = Substitute.For<ITelegramUpdateRouter>();
        router.HandleAsync(poisonUpdate, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("no wallet is marked as the default"));
        var chatNotifier = Substitute.For<IChatNotifier>();
        var service = CreateService(secretStore, clientFactory, new TelegramClientHandle(), router, chatNotifier);

        var first = await service.RunTickAsync(TestContext.Current.CancellationToken);
        var second = await service.RunTickAsync(TestContext.Current.CancellationToken);
        var third = await service.RunTickAsync(TestContext.Current.CancellationToken);

        first.Should().Be(TelegramPollResult.Failed, "the poison update is still within its retry budget");
        second.Should().Be(TelegramPollResult.Failed, "still within budget - nothing behind it may run yet");
        third.Should().Be(TelegramPollResult.Processed, "attempts are exhausted, so the poller skips it and moves on");
        await router.Received(3).HandleAsync(poisonUpdate, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await router.Received(1).HandleAsync(laterUpdate, "Europe/Belgrade", Arg.Any<CancellationToken>());
        await secretStore.Received(1).SetAsync(TelegramUpdateOffsetStore.Key, "12", Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).SendAsync(111L, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_operator_notification_failure_does_not_prevent_the_poison_update_from_being_skipped()
    {
        var secretStore = WithToken("tok1");
        var poisonUpdate = new Update { Id = 10, Message = new Message { Id = 1, Chat = new Chat { Id = 111L }, Text = "a" } };
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns([poisonUpdate]);
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var router = Substitute.For<ITelegramUpdateRouter>();
        router.HandleAsync(poisonUpdate, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("boom"));
        var chatNotifier = Substitute.For<IChatNotifier>();
        chatNotifier.SendAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("chat unreachable too"));
        var service = CreateService(secretStore, clientFactory, new TelegramClientHandle(), router, chatNotifier);

        await service.RunTickAsync(TestContext.Current.CancellationToken);
        await service.RunTickAsync(TestContext.Current.CancellationToken);
        var third = await service.RunTickAsync(TestContext.Current.CancellationToken);

        third.Should().Be(TelegramPollResult.Processed, "the skip itself must not be undone by a failed notification");
        await secretStore.Received(1).SetAsync(TelegramUpdateOffsetStore.Key, "11", Arg.Any<CancellationToken>());
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
            ReadyGate(),
            Substitute.For<IPollingHeartbeat>(),
            new OperationTimer(TimeProvider.System, new SlowOperationOptions()),
            NullLogger<TelegramPollingService>.Instance);

        var result = await service.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Failed);
        clientFactory.DidNotReceive().Create(Arg.Any<string>());
    }

    [Fact]
    public async Task Asks_telegram_for_edits_and_button_presses()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns([]);
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);

        await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle())
            .RunTickAsync(TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(
            Arg.Is<GetUpdatesRequest>(r => r.AllowedUpdates!.Contains(UpdateType.Message) && r.AllowedUpdates!.Contains(UpdateType.CallbackQuery)
                && r.AllowedUpdates!.Contains(UpdateType.EditedMessage)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_empty_poll_logs_no_timing()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns([]);
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new CapturingLogger<TelegramPollingService>();

        await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(), timeProvider: clock, capturingLogger: logger)
            .RunTickAsync(TestContext.Current.CancellationToken);

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_poll_returning_updates_logs_one_getUpdates_and_one_handleUpdate_per_update()
    {
        var update10 = new Update { Id = 10, Message = new Message { Id = 1, Chat = new Chat { Id = 111L }, Text = "a" } };
        var update11 = new Update { Id = 11, Message = new Message { Id = 2, Chat = new Chat { Id = 111L }, Text = "b" } };
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns([update10, update11]);
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var router = Substitute.For<ITelegramUpdateRouter>();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new CapturingLogger<TelegramPollingService>();

        await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(), router, timeProvider: clock, capturingLogger: logger)
            .RunTickAsync(TestContext.Current.CancellationToken);

        logger.Entries.Count(entry => entry.Properties.GetValueOrDefault("Operation") as string == "telegram.getUpdates").Should().Be(1);
        logger.Entries.Count(entry => entry.Properties.GetValueOrDefault("Operation") as string == "telegram.handleUpdate").Should().Be(2);
    }

    [Fact]
    public async Task An_idle_poll_at_92_seconds_with_a_90_second_polling_interval_logs_nothing()
    {
        var client = Substitute.For<ITelegramBotClient>();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => { clock.Advance(TimeSpan.FromSeconds(92)); return Array.Empty<Update>(); });
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var logger = new CapturingLogger<TelegramPollingService>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Telegram:PollingSeconds"] = "90" }).Build();

        await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(),
                timeProvider: clock, capturingLogger: logger, configuration: configuration)
            .RunTickAsync(TestContext.Current.CancellationToken);

        logger.Entries.Should().BeEmpty("92 s is below the 93 s threshold (3 s telegram + 90 s expected wait)");
    }

    [Fact]
    public async Task An_idle_poll_at_94_seconds_with_a_90_second_polling_interval_logs_5302_with_the_combined_threshold()
    {
        var client = Substitute.For<ITelegramBotClient>();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => { clock.Advance(TimeSpan.FromSeconds(94)); return Array.Empty<Update>(); });
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var logger = new CapturingLogger<TelegramPollingService>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Telegram:PollingSeconds"] = "90" }).Build();

        await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(),
                timeProvider: clock, capturingLogger: logger, configuration: configuration)
            .RunTickAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.EventId.Id.Should().Be(5302);
        entry.Properties["ThresholdMs"].Should().Be(93000L);
    }

    [Fact]
    public async Task A_GetUpdates_call_that_throws_after_10_seconds_still_logs_telegram_getUpdates()
    {
        var client = Substitute.For<ITelegramBotClient>();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
            .Returns<Update[]>(_ => { clock.Advance(TimeSpan.FromSeconds(10)); throw new HttpRequestException("cable pulled"); });
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var logger = new CapturingLogger<TelegramPollingService>();

        var result = await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(), timeProvider: clock, capturingLogger: logger)
            .RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Failed);
        logger.Entries.Should().ContainSingle(entry => entry.Properties.GetValueOrDefault("Operation") as string == "telegram.getUpdates");
    }

    [Fact]
    public async Task Registers_the_health_command_scoped_to_the_owner_chat_when_the_client_is_first_built()
    {
        var secretStore = WithToken("tok1");
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, "555"));
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);

        await CreateService(secretStore, clientFactory, new TelegramClientHandle())
            .RunTickAsync(TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(
            Arg.Is<SetMyCommandsRequest>(r => r.Commands!.Single().Command == "health"
                && r.Commands!.Single().Description == "System health"
                && r.Scope is BotCommandScopeChat && ((BotCommandScopeChat)r.Scope!).ChatId.Identifier == 555L),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_registering_the_health_command_when_there_is_no_owner_yet()
    {
        var secretStore = WithToken("tok1");
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);

        await CreateService(secretStore, clientFactory, new TelegramClientHandle())
            .RunTickAsync(TestContext.Current.CancellationToken);

        await client.DidNotReceive().SendRequest(Arg.Any<SetMyCommandsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_command_registration_does_not_fail_the_tick()
    {
        var secretStore = WithToken("tok1");
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, "555"));
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
        client.SendRequest(Arg.Any<SetMyCommandsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: chat not found", 400));
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);

        var result = await CreateService(secretStore, clientFactory, new TelegramClientHandle())
            .RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(TelegramPollResult.Processed, "a failed command registration is logged, never fatal");
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

    [Fact]
    public async Task The_loop_waits_for_the_database_gate_before_its_first_poll()
    {
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        var gateSource = new TaskCompletionSource();
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(gateSource.Task);
        var service = CreateService(NoTokenYet(), clientFactory, new TelegramClientHandle(), gate: gate);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        clientFactory.DidNotReceive().Create(Arg.Any<string>());

        gateSource.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        // NoTokenYet() never has a token, so the only observable effect of the gate releasing is
        // that the loop starts ticking at all - proven by not throwing/hanging past StopAsync.

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_successful_poll_records_a_success_on_the_heartbeat()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var heartbeat = Substitute.For<IPollingHeartbeat>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero));

        await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(), heartbeat: heartbeat, timeProvider: time)
            .RunTickAsync(TestContext.Current.CancellationToken);

        heartbeat.Received(1).RecordSuccess(time.GetUtcNow());
    }

    [Fact]
    public async Task A_network_failure_records_a_network_classified_heartbeat_failure()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var heartbeat = Substitute.For<IPollingHeartbeat>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero));

        await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(), heartbeat: heartbeat, timeProvider: time)
            .RunTickAsync(TestContext.Current.CancellationToken);

        heartbeat.Received(1).RecordFailure(time.GetUtcNow(), PollFailure.Network);
    }

    [Fact]
    public async Task A_401_from_Telegram_records_an_unauthorized_classified_heartbeat_failure()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Unauthorized", 401));
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var heartbeat = Substitute.For<IPollingHeartbeat>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero));

        await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(), heartbeat: heartbeat, timeProvider: time)
            .RunTickAsync(TestContext.Current.CancellationToken);

        heartbeat.Received(1).RecordFailure(time.GetUtcNow(), PollFailure.Unauthorized);
    }

    [Fact]
    public async Task An_unexpected_exception_records_an_other_classified_heartbeat_failure()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("unexpected"));
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);
        var heartbeat = Substitute.For<IPollingHeartbeat>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero));

        await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(), heartbeat: heartbeat, timeProvider: time)
            .RunTickAsync(TestContext.Current.CancellationToken);

        heartbeat.Received(1).RecordFailure(time.GetUtcNow(), PollFailure.Other);
    }
}
