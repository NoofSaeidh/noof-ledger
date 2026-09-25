using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Domain;
using Noof.Ledger.TestKit;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramUpdateRouterTests
{
    static readonly IRecordEcho Echo = new RecordEcho();

    sealed record Harness(
        TelegramUpdateRouter Router, ICaptureStore CaptureStore, IChatNotifier ChatNotifier,
        IRecordEditor Editor, ICategorizationStore Store, CapturingLogger<TelegramUpdateRouter> Logger);

    static Harness CreateRouter(long ownerChatId)
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, ownerChatId.ToString()));
        var captureStore = Substitute.For<ICaptureStore>();
        var chatNotifier = Substitute.For<IChatNotifier>();
        var editor = Substitute.For<IRecordEditor>();
        var store = Substitute.For<ICategorizationStore>();
        var logger = new CapturingLogger<TelegramUpdateRouter>();
        var router = new TelegramUpdateRouter(captureStore, chatNotifier, new TelegramOwnerGate(secretStore),
            new RecordActionHandler(editor, store, chatNotifier, Echo), new CorrectionHandler(editor, chatNotifier, Echo), Echo,
            Substitute.For<ISystemHealth>(), logger);

        return new Harness(router, captureStore, chatNotifier, editor, store, logger);
    }

    static (TelegramUpdateRouter Router, IChatNotifier ChatNotifier, ISystemHealth SystemHealth, ISecretStore SecretStore)
        CreateHealthHarness(SecretResult ownerSecret)
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>()).Returns(ownerSecret);
        var captureStore = Substitute.For<ICaptureStore>();
        var chatNotifier = Substitute.For<IChatNotifier>();
        var editor = Substitute.For<IRecordEditor>();
        var store = Substitute.For<ICategorizationStore>();
        var systemHealth = Substitute.For<ISystemHealth>();
        var logger = new CapturingLogger<TelegramUpdateRouter>();
        var router = new TelegramUpdateRouter(captureStore, chatNotifier, new TelegramOwnerGate(secretStore),
            new RecordActionHandler(editor, store, chatNotifier, Echo), new CorrectionHandler(editor, chatNotifier, Echo), Echo,
            systemHealth, logger);

        return (router, chatNotifier, systemHealth, secretStore);
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
        var (router, captureStore, chatNotifier, _, _, _) = CreateRouter(ownerChatId: 111L);
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
    public async Task Received_is_logged_for_a_text_capture_with_the_transaction_scope()
    {
        var (router, captureStore, chatNotifier, _, _, logger) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>())
            .Returns(transactionId);
        chatNotifier.SendAsync(111L, Echo.Acknowledgement, Arg.Any<CancellationToken>()).Returns(777);

        await router.HandleAsync(
            TextMessage(111L, 5, "coffee 3.20 EUR", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.ReceivedEventId).Subject;
        entry.Stage.Should().Be(TransactionStages.Received);
        entry.Properties["CaptureKind"].Should().Be(CaptureKind.Text);
        entry.Properties["ChatId"].Should().Be(111L);
        entry.Scope.Should().NotBeNull();
        entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(transactionId);
    }

    [Fact]
    public async Task Received_is_logged_for_a_voice_capture()
    {
        var (router, captureStore, chatNotifier, _, _, logger) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        captureStore.CaptureVoiceAsync(Arg.Any<CapturedVoice>(), "Europe/Belgrade", Arg.Any<CancellationToken>())
            .Returns(transactionId);
        chatNotifier.SendAsync(111L, Echo.Transcribing, Arg.Any<CancellationToken>()).Returns(778);
        var update = new Update
        {
            Id = 901,
            Message = new Message
            {
                Id = 6, Chat = new Chat { Id = 111L }, Date = DateTime.UtcNow,
                Voice = new Voice { FileId = "voice-1", Duration = 4 },
            },
        };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.ReceivedEventId).Subject;
        entry.Properties["CaptureKind"].Should().Be(CaptureKind.Voice);
        entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(transactionId);
    }

    [Fact]
    public async Task A_failed_acknowledgement_logs_StageFailed_for_Received_and_rethrows()
    {
        var (router, captureStore, chatNotifier, _, _, logger) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>())
            .Returns(transactionId);
        chatNotifier.SendAsync(111L, Echo.Acknowledgement, Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("bot token revoked"));

        var act = () => router.HandleAsync(
            TextMessage(111L, 5, "coffee 3.20 EUR", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.StageFailedEventId).Subject;
        entry.Properties["FailedStage"].Should().Be(TransactionStages.Received);
        entry.Exception.Should().NotBeNull();
        entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(transactionId);
    }

    [Fact]
    public async Task Stores_the_time_Telegram_sent_the_message_not_the_time_it_was_processed()
    {
        // An outage can queue a message for hours; Telegram's own Message.Date is when the spend
        // happened, "now" at handling time is only when we got around to it. Collapsing every
        // queued message onto the reconnection moment buckets it into the wrong local day once
        // Phase 1B reads time_zone_id back to compute "today" / "this month".
        var (router, captureStore, chatNotifier, _, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, chatNotifier, _, _, _) = CreateRouter(ownerChatId: 111L);

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
        var (router, captureStore, _, _, _, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(new Update { Id = 901 }, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ignores_a_message_with_no_text()
    {
        var (router, captureStore, _, _, _, _) = CreateRouter(ownerChatId: 111L);
        var update = new Update { Id = 902, Message = new Message { Id = 6, Chat = new Chat { Id = 111L } } };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_cancels_the_record_and_turns_its_echo_into_the_cancelled_one()
    {
        var (router, _, chatNotifier, editor, store, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, _, chatNotifier, editor, store, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, _, chatNotifier, editor, _, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(ButtonPress(999L, 42, "cancel"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.DidNotReceive().AnswerActionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await editor.DidNotReceive().CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_press_on_a_message_that_is_no_record_is_answered_and_nothing_else()
    {
        var (router, _, chatNotifier, editor, _, _) = CreateRouter(ownerChatId: 111L);
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);

        await router.HandleAsync(ButtonPress(111L, 42, "cancel"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.Received(1).AnswerActionAsync("cb-1", Arg.Any<CancellationToken>());
        await editor.DidNotReceive().CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await chatNotifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reply_to_an_echo_queues_a_correction_instead_of_a_new_capture()
    {
        var (router, captureStore, chatNotifier, editor, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, chatNotifier, editor, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, _, editor, _, _) = CreateRouter(ownerChatId: 111L);
        editor.FindByBotMessageAsync(111L, 3, Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);

        await router.HandleAsync(ReplyTo(111L, 8, 3, "хлеб 100"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureAsync(Arg.Is<CapturedMessage>(m => m.Text == "хлеб 100"), "Europe/Belgrade", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_asks_what_to_change_and_remembers_the_prompt()
    {
        var (router, _, chatNotifier, editor, _, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        chatNotifier.AskAsync(111L, 42, Echo.EditPrompt, Arg.Any<CancellationToken>()).Returns(77);

        await router.HandleAsync(ButtonPress(111L, 42, "edit"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).AttachPromptAsync(transactionId, 77, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Editing_the_original_message_re_reads_it()
    {
        var (router, _, chatNotifier, editor, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, _, _, editor, _, _) = CreateRouter(ownerChatId: 111L);
        var update = new Update { Id = 906, EditedMessage = new Message { Id = 5, Chat = new Chat { Id = 999L }, Text = "x" } };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.DidNotReceive().FindByUserMessageAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    static Update VoiceNote(long chatId, int messageId, DateTime date, Message? replyTo = null) => new()
    {
        Id = 905,
        Message = new Message
        {
            Id = messageId,
            Chat = new Chat { Id = chatId },
            Date = date,
            ReplyToMessage = replyTo,
            Voice = new Voice { FileId = "voice-file-1", FileUniqueId = "unique-1", Duration = 4 },
        },
    };

    [Fact]
    public async Task Captures_a_voice_note_and_says_it_is_transcribing()
    {
        var (router, captureStore, chatNotifier, _, _, _) = CreateRouter(ownerChatId: 111L);
        var sentAt = DateTimeOffset.Parse("2026-09-24T21:30:00Z");
        var transactionId = Guid.NewGuid();
        captureStore.CaptureVoiceAsync(Arg.Any<CapturedVoice>(), "Europe/Belgrade", Arg.Any<CancellationToken>()).Returns(transactionId);
        chatNotifier.SendAsync(111L, Echo.Transcribing, Arg.Any<CancellationToken>()).Returns(777);

        await router.HandleAsync(VoiceNote(111L, 5, sentAt.UtcDateTime), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureVoiceAsync(
            Arg.Is<CapturedVoice>(v => v.ChatId == 111L && v.MessageId == 5 && v.VoiceFileId == "voice-file-1"
                && v.DurationSeconds == 4 && v.SentAt == sentAt),
            "Europe/Belgrade",
            Arg.Any<CancellationToken>());
        await captureStore.Received(1).AttachBotMessageAsync(transactionId, 777, Arg.Any<CancellationToken>());
        await captureStore.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_strangers_voice_note_before_reading_it()
    {
        var (router, captureStore, chatNotifier, editor, _, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(VoiceNote(222L, 5, DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceiveWithAnyArgs().CaptureVoiceAsync(default!, default!, Arg.Any<CancellationToken>());
        await chatNotifier.DidNotReceiveWithAnyArgs().SendAsync(default, default!, Arg.Any<CancellationToken>());
        await editor.DidNotReceiveWithAnyArgs().FindByBotMessageAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_voice_reply_to_an_echo_queues_a_spoken_correction_instead_of_a_new_capture()
    {
        var (router, captureStore, chatNotifier, editor, _, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        var sentAt = DateTimeOffset.Parse("2026-09-24T10:00:00Z");
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.RequestVoiceCorrectionAsync(transactionId, "voice-file-1", 6, sentAt, Arg.Any<CancellationToken>()).Returns(true);

        await router.HandleAsync(
            VoiceNote(111L, 6, sentAt.UtcDateTime, replyTo: new Message { Id = 42, Chat = new Chat { Id = 111L } }),
            "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).RequestVoiceCorrectionAsync(transactionId, "voice-file-1", 6, sentAt, Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m => m.Text == Echo.Transcribing), Arg.Any<CancellationToken>());
        await captureStore.DidNotReceiveWithAnyArgs().CaptureVoiceAsync(default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_redelivered_voice_reply_edits_nothing_and_captures_nothing()
    {
        var (router, captureStore, chatNotifier, editor, _, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.RequestVoiceCorrectionAsync(transactionId, "voice-file-1", 6, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await router.HandleAsync(
            VoiceNote(111L, 6, DateTime.UtcNow, replyTo: new Message { Id = 42, Chat = new Chat { Id = 111L } }),
            "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.DidNotReceiveWithAnyArgs().EditAsync(default, default, default!, Arg.Any<CancellationToken>());
        await captureStore.DidNotReceiveWithAnyArgs().CaptureVoiceAsync(default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_voice_reply_to_anything_but_an_echo_is_captured_as_a_new_voice_note()
    {
        var (router, captureStore, _, editor, _, _) = CreateRouter(ownerChatId: 111L);
        editor.FindByBotMessageAsync(111L, 50, Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);

        await router.HandleAsync(
            VoiceNote(111L, 6, DateTime.UtcNow, replyTo: new Message { Id = 50, Chat = new Chat { Id = 111L } }),
            "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureVoiceAsync(Arg.Any<CapturedVoice>(), "Europe/Belgrade", Arg.Any<CancellationToken>());
        await editor.DidNotReceiveWithAnyArgs().RequestVoiceCorrectionAsync(default, default!, default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_edited_voice_note_is_ignored()
    {
        var (router, _, _, editor, _, _) = CreateRouter(ownerChatId: 111L);
        var edited = new Update
        {
            Id = 906,
            EditedMessage = new Message
            {
                Id = 5, Chat = new Chat { Id = 111L }, Date = DateTime.UtcNow, Caption = "новая подпись",
                Voice = new Voice { FileId = "voice-file-1", FileUniqueId = "unique-1", Duration = 4 },
            },
        };

        await router.HandleAsync(edited, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.DidNotReceiveWithAnyArgs().FindByUserMessageAsync(default, default, Arg.Any<CancellationToken>());
        await editor.DidNotReceiveWithAnyArgs().ReplaceRawTextAsync(default, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_owner_gets_a_formatted_health_reply_and_captures_nothing()
    {
        var (router, chatNotifier, systemHealth, _) = CreateHealthHarness(new SecretResult(SecretState.Present, "111"));
        var report = new SystemHealthReport(HealthLevel.Ok,
            [new HealthItem("Database", HealthLevel.Ok, "ready", DateTimeOffset.Parse("2026-09-25T10:00:00Z"))]);
        systemHealth.GetAsync(true, Arg.Any<CancellationToken>()).Returns(report);

        await router.HandleAsync(TextMessage(111L, 5, "/health", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.Received(1).SendAsync(111L, HealthReplyFormatter.Format(report), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Health_at_bot_suffix_is_recognised_as_the_command()
    {
        var (router, chatNotifier, systemHealth, _) = CreateHealthHarness(new SecretResult(SecretState.Present, "111"));
        var report = new SystemHealthReport(HealthLevel.Ok, []);
        systemHealth.GetAsync(true, Arg.Any<CancellationToken>()).Returns(report);

        await router.HandleAsync(TextMessage(111L, 5, "/HEALTH@my_ledger_bot", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.Received(1).SendAsync(111L, HealthReplyFormatter.Format(report), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_strangers_health_command_gets_no_reply_and_never_reads_system_health()
    {
        var (router, chatNotifier, systemHealth, _) = CreateHealthHarness(new SecretResult(SecretState.Present, "111"));

        await router.HandleAsync(TextMessage(999L, 5, "/health", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.DidNotReceiveWithAnyArgs().SendAsync(default, default!, Arg.Any<CancellationToken>());
        await systemHealth.DidNotReceiveWithAnyArgs().GetAsync(default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_health_command_with_no_owner_yet_is_silent_and_never_claims_ownership()
    {
        var (router, chatNotifier, systemHealth, secretStore) = CreateHealthHarness(new SecretResult(SecretState.Missing, null));

        await router.HandleAsync(TextMessage(111L, 5, "/health", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.DidNotReceiveWithAnyArgs().SendAsync(default, default!, Arg.Any<CancellationToken>());
        await systemHealth.DidNotReceiveWithAnyArgs().GetAsync(default, Arg.Any<CancellationToken>());
        await secretStore.DidNotReceive().TrySetIfMissingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Text_that_merely_starts_with_health_but_is_not_the_exact_command_is_still_captured()
    {
        var (router, captureStore, chatNotifier, _, _, _) = CreateRouter(ownerChatId: 111L);
        captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        chatNotifier.SendAsync(111L, Echo.Acknowledgement, Arg.Any<CancellationToken>()).Returns(777);

        await router.HandleAsync(TextMessage(111L, 5, "/healthclub 500", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureAsync(
            Arg.Is<CapturedMessage>(m => m.Text == "/healthclub 500"), "Europe/Belgrade", Arg.Any<CancellationToken>());
    }
}
