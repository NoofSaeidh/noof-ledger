using NSubstitute;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Domain;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramUpdateRouterTests
{
    static readonly IRecordEcho Echo = new RecordEcho();

    sealed record Harness(
        TelegramUpdateRouter Router, ICaptureStore CaptureStore, IChatNotifier ChatNotifier,
        IRecordEditor Editor, ICategorizationStore Store);

    static Harness CreateRouter(long ownerChatId)
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, ownerChatId.ToString()));
        var captureStore = Substitute.For<ICaptureStore>();
        var chatNotifier = Substitute.For<IChatNotifier>();
        var editor = Substitute.For<IRecordEditor>();
        var store = Substitute.For<ICategorizationStore>();
        var router = new TelegramUpdateRouter(captureStore, chatNotifier, new TelegramOwnerGate(secretStore),
            new RecordActionHandler(editor, store, chatNotifier, Echo), new CorrectionHandler(editor, chatNotifier, Echo), Echo);

        return new Harness(router, captureStore, chatNotifier, editor, store);
    }

    static Update TextMessage(long chatId, int messageId, string text, DateTime date) => new()
    {
        Id = 900,
        Message = new Message { Id = messageId, Chat = new Chat { Id = chatId }, Text = text, Date = date },
    };

    static Update ReplyTo(long chatId, int replyId, int repliedToId, string text) => new()
    {
        Id = 904,
        Message = new Message
        {
            Id = replyId,
            Chat = new Chat { Id = chatId },
            Text = text,
            Date = DateTime.UtcNow,
            ReplyToMessage = new Message { Id = repliedToId, Chat = new Chat { Id = chatId } },
        },
    };

    static Update ButtonPress(long chatId, int echoId, string data) => new()
    {
        Id = 903,
        CallbackQuery = new CallbackQuery
        {
            Id = "cb-1",
            Data = data,
            From = new User { Id = chatId },
            Message = new Message { Id = echoId, Chat = new Chat { Id = chatId } },
        },
    };

    static CategorizationSubject RecordWith(TransactionStatus status) =>
        new(Guid.NewGuid(), "кофе 250", 111L, 42, "Cash", status, new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 22),
            [new RecordedLine("кофе", new Money(250m, CurrencyCode.Rsd), "food-drink", "Food & Drink", null)]);

    [Fact]
    public async Task Captures_replies_and_attaches_the_reply_for_the_owner()
    {
        var (router, captureStore, chatNotifier, _, _) = CreateRouter(ownerChatId: 111L);
        var sentAt = DateTimeOffset.Parse("2026-09-21T10:00:00Z");
        var transactionId = Guid.NewGuid();
        captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>())
            .Returns(transactionId);
        chatNotifier.SendAsync(111L, Echo.Acknowledgement, Arg.Any<CancellationToken>())
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
        var (router, captureStore, chatNotifier, _, _) = CreateRouter(ownerChatId: 111L);
        var sentAt = DateTimeOffset.Parse("2026-09-21T23:50:00Z");
        captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        chatNotifier.SendAsync(111L, Echo.Acknowledgement, Arg.Any<CancellationToken>())
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
        var (router, captureStore, chatNotifier, _, _) = CreateRouter(ownerChatId: 111L);

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
        var (router, captureStore, _, _, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(new Update { Id = 901 }, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ignores_a_message_with_no_text()
    {
        var (router, captureStore, _, _, _) = CreateRouter(ownerChatId: 111L);
        var update = new Update { Id = 902, Message = new Message { Id = 6, Chat = new Chat { Id = 111L } } };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_cancels_the_record_and_turns_its_echo_into_the_cancelled_one()
    {
        var (router, _, chatNotifier, editor, store) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        store.GetSubjectAsync(transactionId, Arg.Any<CancellationToken>()).Returns(RecordWith(TransactionStatus.Cancelled));

        await router.HandleAsync(ButtonPress(111L, 42, "cancel"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).CancelAsync(transactionId, Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).AnswerActionAsync("cb-1", Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(echo => echo.Text.StartsWith("Cancelled") && echo.Actions.SequenceEqual(new[] { RecordAction.Restore })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restore_restores_the_record_and_its_buttons()
    {
        var (router, _, chatNotifier, editor, store) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        store.GetSubjectAsync(transactionId, Arg.Any<CancellationToken>()).Returns(RecordWith(TransactionStatus.Completed));

        await router.HandleAsync(ButtonPress(111L, 42, "restore"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).RestoreAsync(transactionId, Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(echo => echo.Actions.SequenceEqual(new[] { RecordAction.Cancel, RecordAction.Edit })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_strangers_button_press_is_ignored_entirely()
    {
        var (router, _, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(ButtonPress(999L, 42, "cancel"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.DidNotReceive().AnswerActionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await editor.DidNotReceive().CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_press_on_a_message_that_is_no_record_is_answered_and_nothing_else()
    {
        var (router, _, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);

        await router.HandleAsync(ButtonPress(111L, 42, "cancel"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.Received(1).AnswerActionAsync("cb-1", Arg.Any<CancellationToken>());
        await editor.DidNotReceive().CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await chatNotifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reply_to_an_echo_queues_a_correction_instead_of_a_new_capture()
    {
        var (router, captureStore, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.RequestCorrectionAsync(transactionId, "нет, 1500", 8, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);

        await router.HandleAsync(ReplyTo(111L, 8, 42, "нет, 1500"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).RequestCorrectionAsync(transactionId, "нет, 1500", 8, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(echo => echo.Text == Echo.Correcting && echo.Actions.Count == 0), Arg.Any<CancellationToken>());
        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_redelivered_reply_edits_nothing_and_captures_nothing()
    {
        var (router, captureStore, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.RequestCorrectionAsync(transactionId, "нет, 1500", 8, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);

        await router.HandleAsync(ReplyTo(111L, 8, 42, "нет, 1500"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reply_to_anything_but_an_echo_is_captured_as_a_new_message()
    {
        var (router, captureStore, _, editor, _) = CreateRouter(ownerChatId: 111L);
        editor.FindByBotMessageAsync(111L, 3, Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);

        await router.HandleAsync(ReplyTo(111L, 8, 3, "хлеб 100"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureAsync(Arg.Is<CapturedMessage>(m => m.Text == "хлеб 100"), "Europe/Belgrade", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_asks_what_to_change_and_remembers_the_prompt()
    {
        var (router, _, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        chatNotifier.AskAsync(111L, 42, Echo.EditPrompt, Arg.Any<CancellationToken>()).Returns(77);

        await router.HandleAsync(ButtonPress(111L, 42, "edit"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).AttachPromptAsync(transactionId, 77, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Editing_the_original_message_re_reads_it()
    {
        var (router, _, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByUserMessageAsync(111L, 5, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.ReplaceRawTextAsync(transactionId, "кофе 300", Arg.Any<CancellationToken>()).Returns(true);
        var update = new Update { Id = 905, EditedMessage = new Message { Id = 5, Chat = new Chat { Id = 111L }, Text = "кофе 300" } };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).ReplaceRawTextAsync(transactionId, "кофе 300", Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(echo => echo.Text == Echo.Correcting), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_strangers_edit_is_ignored()
    {
        var (router, _, _, editor, _) = CreateRouter(ownerChatId: 111L);
        var update = new Update { Id = 906, EditedMessage = new Message { Id = 5, Chat = new Chat { Id = 999L }, Text = "x" } };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.DidNotReceive().FindByUserMessageAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
