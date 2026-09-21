using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Telegram;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramUpdateRouterTests
{
    static (TelegramUpdateRouter Router, ICaptureStore CaptureStore, IChatNotifier ChatNotifier) CreateRouter(long ownerChatId)
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, ownerChatId.ToString()));
        var captureStore = Substitute.For<ICaptureStore>();
        var chatNotifier = Substitute.For<IChatNotifier>();
        var router = new TelegramUpdateRouter(captureStore, chatNotifier, new TelegramOwnerGate(secretStore));

        return (router, captureStore, chatNotifier);
    }

    static Update TextMessage(long chatId, int messageId, string text, DateTime date) => new()
    {
        Id = 900,
        Message = new Message { Id = messageId, Chat = new Chat { Id = chatId }, Text = text, Date = date },
    };

    [Fact]
    public async Task Captures_replies_and_attaches_the_reply_for_the_owner()
    {
        var (router, captureStore, chatNotifier) = CreateRouter(ownerChatId: 111L);
        var sentAt = DateTimeOffset.Parse("2026-09-21T10:00:00Z");
        var transactionId = Guid.NewGuid();
        captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>())
            .Returns(transactionId);
        chatNotifier.SendAsync(111L, TelegramUpdateRouter.ReceiptAcknowledgement, Arg.Any<CancellationToken>())
            .Returns(777);

        await router.HandleAsync(
            TextMessage(111L, 5, "coffee 3.20 EUR", sentAt.UtcDateTime),
            "Europe/Belgrade",
            TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureAsync(
            Arg.Is<CapturedMessage>(m => m.ChatId == 111L && m.MessageId == 5 && m.Text == "coffee 3.20 EUR" && m.SentAt == sentAt),
            "Europe/Belgrade",
            Arg.Any<CancellationToken>());
        await captureStore.Received(1).AttachBotMessageAsync(transactionId, 777, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stores_the_time_Telegram_sent_the_message_not_the_time_it_was_processed()
    {
        // An outage can queue a message for hours; Telegram's own Message.Date is when the spend
        // happened, "now" at handling time is only when we got around to it. Collapsing every
        // queued message onto the reconnection moment buckets it into the wrong local day once
        // Phase 1B reads time_zone_id back to compute "today" / "this month".
        var (router, captureStore, chatNotifier) = CreateRouter(ownerChatId: 111L);
        var sentAt = DateTimeOffset.Parse("2026-09-21T23:50:00Z");
        captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        chatNotifier.SendAsync(111L, TelegramUpdateRouter.ReceiptAcknowledgement, Arg.Any<CancellationToken>())
            .Returns(777);

        // "Processed" hours after "sent" -- exactly the outage-recovery scenario this guards.
        await router.HandleAsync(
            TextMessage(111L, 5, "coffee 3.20 EUR", sentAt.UtcDateTime),
            "Europe/Belgrade",
            TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureAsync(
            Arg.Is<CapturedMessage>(m => m.SentAt == sentAt),
            "Europe/Belgrade",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_stranger_before_capturing_or_replying()
    {
        var (router, captureStore, chatNotifier) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(
            TextMessage(999L, 5, "coffee 3.20 EUR", DateTime.UtcNow),
            "Europe/Belgrade",
            TestContext.Current.CancellationToken);

        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await chatNotifier.DidNotReceive().SendAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ignores_an_update_with_no_message()
    {
        var (router, captureStore, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(new Update { Id = 901 }, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ignores_a_message_with_no_text()
    {
        var (router, captureStore, _) = CreateRouter(ownerChatId: 111L);
        var update = new Update { Id = 902, Message = new Message { Id = 6, Chat = new Chat { Id = 111L } } };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
