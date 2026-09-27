using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Domain;
using Noof.Ledger.TestKit;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramUpdateRouterTests
{
    static readonly IRecordEcho Echo = new RecordEcho();
    static readonly IFiscalVerificationUrl VerificationUrl =
        new FiscalVerificationUrl(new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });

    sealed record Harness(
        TelegramUpdateRouter Router, ICaptureStore CaptureStore, IChatNotifier ChatNotifier,
        IRecordEditor Editor, ICategorizationStore Store, CapturingLogger<TelegramUpdateRouter> Logger,
        IReceiptStore ReceiptStore);

    static Harness CreateRouter(long ownerChatId)
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, ownerChatId.ToString()));
        var captureStore = Substitute.For<ICaptureStore>();
        var chatNotifier = Substitute.For<IChatNotifier>();
        var editor = Substitute.For<IRecordEditor>();
        var store = Substitute.For<ICategorizationStore>();
        var receiptStore = Substitute.For<IReceiptStore>();
        receiptStore.GetByTransactionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ReceiptView?)null);
        receiptStore.GetVerificationUrlAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var logger = new CapturingLogger<TelegramUpdateRouter>();
        var router = new TelegramUpdateRouter(captureStore, chatNotifier, new TelegramOwnerGate(secretStore),
            new RecordActionHandler(editor, store, chatNotifier, Echo, receiptStore),
            new CorrectionHandler(editor, chatNotifier, Echo, receiptStore, VerificationUrl), Echo,
            Substitute.For<ISystemHealth>(), VerificationUrl, logger);

        return new Harness(router, captureStore, chatNotifier, editor, store, logger, receiptStore);
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
        var receiptStore = Substitute.For<IReceiptStore>();
        receiptStore.GetByTransactionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ReceiptView?)null);
        receiptStore.GetVerificationUrlAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var systemHealth = Substitute.For<ISystemHealth>();
        var logger = new CapturingLogger<TelegramUpdateRouter>();
        var router = new TelegramUpdateRouter(captureStore, chatNotifier, new TelegramOwnerGate(secretStore),
            new RecordActionHandler(editor, store, chatNotifier, Echo, receiptStore),
            new CorrectionHandler(editor, chatNotifier, Echo, receiptStore, VerificationUrl), Echo,
            systemHealth, VerificationUrl, logger);

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
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, chatNotifier, _, _, logger, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, chatNotifier, _, _, logger, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, chatNotifier, _, _, logger, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);

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
        var (router, captureStore, _, _, _, _, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(new Update { Id = 901 }, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ignores_a_message_with_no_text()
    {
        var (router, captureStore, _, _, _, _, _) = CreateRouter(ownerChatId: 111L);
        var update = new Update { Id = 902, Message = new Message { Id = 6, Chat = new Chat { Id = 111L } } };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_cancels_the_record_and_turns_its_echo_into_the_cancelled_one()
    {
        var (router, _, chatNotifier, editor, store, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, _, chatNotifier, editor, store, _, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        store.GetSubjectAsync(transactionId, Arg.Any<CancellationToken>()).Returns(RecordWith(TransactionStatus.Completed));

        await router.HandleAsync(ButtonPress(111L, 42, "restore"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).RestoreAsync(transactionId, Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(echo => echo.Actions.SequenceEqual(new[] { RecordAction.Cancel, RecordAction.Edit })),
            Arg.Any<CancellationToken>());
    }

    static ReceiptView ReceiptFor() => new(
        Guid.NewGuid(), ReceiptSource.FiscalQr, "SYN-100000001", "Test Market", null, null, "SYN-1",
        new DateTimeOffset(2026, 9, 22, 9, 30, 0, TimeSpan.Zero), 250m, CurrencyCode.Rsd,
        ReceiptKind.Sale, PaymentMethod.Card,
        250m, "https://suf.purs.gov.rs/v/?vl=synthetic",
        [new ReceiptLineView(Guid.NewGuid(), 1, "кофе", 1m, null, 250m, 250m, null)]);

    // M-3 (Phase 6 final review): Cancel/Restore on a receipt transaction must re-render the receipt
    // echo (shop header, lines in receipt order) rather than the generic one.
    [Fact]
    public async Task Cancel_on_a_receipt_transaction_re_renders_the_receipt_echo()
    {
        var (router, _, chatNotifier, editor, store, _, receiptStore) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        store.GetSubjectAsync(transactionId, Arg.Any<CancellationToken>())
            .Returns(RecordWith(TransactionStatus.Cancelled) with { CaptureKind = CaptureKind.Photo });
        receiptStore.GetByTransactionAsync(transactionId, Arg.Any<CancellationToken>()).Returns(ReceiptFor());

        await router.HandleAsync(ButtonPress(111L, 42, "cancel"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(echo =>
                echo.Text.StartsWith("Cancelled — Test Market")
                && echo.Actions.SequenceEqual(new[] { RecordAction.Restore })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restore_on_a_receipt_transaction_re_renders_the_receipt_echo()
    {
        var (router, _, chatNotifier, editor, store, _, receiptStore) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        store.GetSubjectAsync(transactionId, Arg.Any<CancellationToken>())
            .Returns(RecordWith(TransactionStatus.Completed) with { CaptureKind = CaptureKind.Photo });
        receiptStore.GetByTransactionAsync(transactionId, Arg.Any<CancellationToken>()).Returns(ReceiptFor());

        await router.HandleAsync(ButtonPress(111L, 42, "restore"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(echo =>
                echo.Text.StartsWith("Recorded — Test Market")
                && echo.Actions.SequenceEqual(new[] { RecordAction.Cancel, RecordAction.Edit })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_strangers_button_press_is_ignored_entirely()
    {
        var (router, _, chatNotifier, editor, _, _, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(ButtonPress(999L, 42, "cancel"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.DidNotReceive().AnswerActionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await editor.DidNotReceive().CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_press_on_a_message_that_is_no_record_is_answered_and_nothing_else()
    {
        var (router, _, chatNotifier, editor, _, _, _) = CreateRouter(ownerChatId: 111L);
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);

        await router.HandleAsync(ButtonPress(111L, 42, "cancel"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.Received(1).AnswerActionAsync("cb-1", Arg.Any<CancellationToken>());
        await editor.DidNotReceive().CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await chatNotifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reply_to_an_echo_queues_a_correction_instead_of_a_new_capture()
    {
        var (router, captureStore, chatNotifier, editor, _, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, chatNotifier, editor, _, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, _, editor, _, _, _) = CreateRouter(ownerChatId: 111L);
        editor.FindByBotMessageAsync(111L, 3, Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);

        await router.HandleAsync(ReplyTo(111L, 8, 3, "хлеб 100"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureAsync(Arg.Is<CapturedMessage>(m => m.Text == "хлеб 100"), "Europe/Belgrade", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_asks_what_to_change_and_remembers_the_prompt()
    {
        var (router, _, chatNotifier, editor, _, _, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        chatNotifier.AskAsync(111L, 42, Echo.EditPrompt, Arg.Any<CancellationToken>()).Returns(77);

        await router.HandleAsync(ButtonPress(111L, 42, "edit"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).AttachPromptAsync(transactionId, 77, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Editing_the_original_message_re_reads_it()
    {
        var (router, _, chatNotifier, editor, _, _, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByUserMessageAsync(111L, 5, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.ReplaceRawTextAsync(transactionId, "кофе 300", Arg.Any<CancellationToken>()).Returns(true);
        var update = new Update { Id = 905, EditedMessage = new Message { Id = 5, Chat = new Chat { Id = 111L }, Text = "кофе 300" } };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).ReplaceRawTextAsync(transactionId, "кофе 300", Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(echo => echo.Text == Echo.Correcting), Arg.Any<CancellationToken>());
    }

    // N-2 (Phase 6 re-review): editing a receipt link-capture's own message into a DIFFERENT fiscal
    // link is a new receipt, not a correction - the record must be left exactly as it was, and the
    // echo must say so, rather than reaching Reinterpret's record_transaction re-read.
    [Fact]
    public async Task Editing_a_link_captures_message_into_a_different_receipt_link_changes_nothing_and_says_so()
    {
        var (router, _, chatNotifier, editor, _, _, receiptStore) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByUserMessageAsync(111L, 5, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        receiptStore.GetVerificationUrlAsync(transactionId, Arg.Any<CancellationToken>())
            .Returns("https://suf.purs.gov.rs/v/?vl=original");
        var update = new Update
        {
            Id = 907,
            EditedMessage = new Message
            {
                Id = 5, Chat = new Chat { Id = 111L }, Text = "https://suf.purs.gov.rs/v/?vl=adifferentone",
            },
        };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.DidNotReceiveWithAnyArgs().ReplaceRawTextAsync(default, default!, Arg.Any<CancellationToken>());
        // R2-2 (Phase 6 second re-review): the notice must NOT overwrite the record's own echo -
        // that would wipe its "Recorded" summary and Cancel/Edit buttons for a record the text itself
        // says is unchanged. It goes out as a separate message instead.
        await chatNotifier.Received(1).SendAsync(111L, Echo.NewReceiptLinkMustBeSentSeparately.Text, Arg.Any<CancellationToken>());
        await chatNotifier.DidNotReceiveWithAnyArgs().EditAsync(default, default, default!, Arg.Any<CancellationToken>());
    }

    // Resending the SAME link (no change at all), or any other edit text, is an ordinary edit -
    // CategorizationWorker's own claim-time routing (ruling F-2) decides from there whether it belongs
    // to the receipt.
    [Fact]
    public async Task Editing_a_link_captures_message_into_an_ordinary_correction_still_reaches_ReplaceRawTextAsync()
    {
        var (router, _, chatNotifier, editor, _, _, receiptStore) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByUserMessageAsync(111L, 5, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.ReplaceRawTextAsync(transactionId, "actually put this under groceries", Arg.Any<CancellationToken>()).Returns(true);
        receiptStore.GetVerificationUrlAsync(transactionId, Arg.Any<CancellationToken>())
            .Returns("https://suf.purs.gov.rs/v/?vl=original");
        var update = new Update
        {
            Id = 908,
            EditedMessage = new Message { Id = 5, Chat = new Chat { Id = 111L }, Text = "actually put this under groceries" },
        };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).ReplaceRawTextAsync(transactionId, "actually put this under groceries", Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m => m.Text == Echo.Correcting), Arg.Any<CancellationToken>());
    }

    // R2-5 (Phase 6 second re-review): Telegram delivers an edited photo's own free text as Caption,
    // not Text, so a receipt photo's caption edit used to be dropped silently at the top of
    // HandleEditAsync - the operator's edited words went nowhere and no echo changed. A caption edit on
    // a photo (or a document, the other receipt-photo entry point) reaches the same claim-time route as
    // any other edit; a Voice message's own caption stays ignored (An_edited_voice_note_is_ignored)
    // because a voice note's correction text is its transcript, not its caption.
    [Fact]
    public async Task Editing_a_receipt_photos_caption_reaches_ReplaceRawTextAsync_the_same_way_as_editing_its_text()
    {
        var (router, _, chatNotifier, editor, _, _, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByUserMessageAsync(111L, 5, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.ReplaceRawTextAsync(transactionId, "actually this was cash", Arg.Any<CancellationToken>()).Returns(true);
        var update = new Update
        {
            Id = 909,
            EditedMessage = new Message
            {
                Id = 5, Chat = new Chat { Id = 111L }, Caption = "actually this was cash",
                Photo = [new PhotoSize { FileId = "receipt-photo", Width = 1280, Height = 1280 }],
            },
        };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).ReplaceRawTextAsync(transactionId, "actually this was cash", Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m => m.Text == Echo.Correcting), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_strangers_edit_is_ignored()
    {
        var (router, _, _, editor, _, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, chatNotifier, editor, _, _, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(VoiceNote(222L, 5, DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceiveWithAnyArgs().CaptureVoiceAsync(default!, default!, Arg.Any<CancellationToken>());
        await chatNotifier.DidNotReceiveWithAnyArgs().SendAsync(default, default!, Arg.Any<CancellationToken>());
        await editor.DidNotReceiveWithAnyArgs().FindByBotMessageAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_voice_reply_to_an_echo_queues_a_spoken_correction_instead_of_a_new_capture()
    {
        var (router, captureStore, chatNotifier, editor, _, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, chatNotifier, editor, _, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, captureStore, _, editor, _, _, _) = CreateRouter(ownerChatId: 111L);
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
        var (router, _, _, editor, _, _, _) = CreateRouter(ownerChatId: 111L);
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
            [new HealthItem("Database", HealthLevel.Ok, "ready", DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "Noof.Ledger.Host.Startup.DatabaseStartupService")]);
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

    static Update PhotoMessage(long chatId, int messageId, string fileId, string? caption = null) => new()
    {
        Id = 910,
        Message = new Message
        {
            Id = messageId,
            Chat = new Chat { Id = chatId },
            Date = DateTime.UtcNow,
            Caption = caption,
            Photo = [new PhotoSize { FileId = "small-" + fileId, Width = 90, Height = 90 }, new PhotoSize { FileId = fileId, Width = 1280, Height = 1280 }],
        },
    };

    static Update DocumentMessage(long chatId, int messageId, string fileId, string? mimeType) => new()
    {
        Id = 911,
        Message = new Message
        {
            Id = messageId,
            Chat = new Chat { Id = chatId },
            Date = DateTime.UtcNow,
            Document = new Document { FileId = fileId, MimeType = mimeType },
        },
    };

    [Fact]
    public async Task A_photo_is_captured_as_a_receipt_with_its_largest_size_and_acknowledged()
    {
        var (router, captureStore, chatNotifier, _, _, logger, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        captureStore.CaptureReceiptAsync(Arg.Any<CapturedReceipt>(), "Europe/Belgrade", Arg.Any<CancellationToken>()).Returns(transactionId);
        chatNotifier.SendAsync(111L, Echo.ReadingReceipt, Arg.Any<CancellationToken>()).Returns(777);

        await router.HandleAsync(PhotoMessage(111L, 5, "photo-1", "lunch"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureReceiptAsync(
            Arg.Is<CapturedReceipt>(r => r.ChatId == 111L && r.MessageId == 5 && r.Caption == "lunch"
                && r.TelegramFileId == "photo-1" && r.VerificationUrl == null),
            "Europe/Belgrade", Arg.Any<CancellationToken>());
        await captureStore.Received(1).AttachBotMessageAsync(transactionId, 777, Arg.Any<CancellationToken>());
        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.ReceivedEventId).Subject;
        entry.Properties["CaptureKind"].Should().Be(CaptureKind.Photo);
        entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(transactionId);
    }

    [Fact]
    public async Task A_photo_whose_caption_contains_a_fiscal_link_still_captures_only_the_file_id()
    {
        // Verification for the CapturedReceipt XOR invariant: a photo's caption is carried as
        // Caption text only, never scanned for a fiscal link, so the file id is the exact source
        // even when the caption happens to contain a link - VerificationUrl stays null.
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        captureStore.CaptureReceiptAsync(Arg.Any<CapturedReceipt>(), "Europe/Belgrade", Arg.Any<CancellationToken>()).Returns(transactionId);
        const string caption = "https://suf.purs.gov.rs/v/?vl=AbCdEf123";

        await router.HandleAsync(PhotoMessage(111L, 5, "photo-1", caption), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureReceiptAsync(
            Arg.Is<CapturedReceipt>(r => r.Caption == caption && r.TelegramFileId == "photo-1" && r.VerificationUrl == null),
            "Europe/Belgrade", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_image_document_is_captured_as_a_receipt()
    {
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        captureStore.CaptureReceiptAsync(Arg.Any<CapturedReceipt>(), "Europe/Belgrade", Arg.Any<CancellationToken>()).Returns(transactionId);
        chatNotifier.SendAsync(111L, Echo.ReadingReceipt, Arg.Any<CancellationToken>()).Returns(778);

        await router.HandleAsync(DocumentMessage(111L, 6, "doc-1", "image/png"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureReceiptAsync(
            Arg.Is<CapturedReceipt>(r => r.TelegramFileId == "doc-1"), "Europe/Belgrade", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_non_image_document_is_rejected_and_nothing_is_captured()
    {
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(DocumentMessage(111L, 6, "doc-1", "application/pdf"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceiveWithAnyArgs().CaptureReceiptAsync(default!, default!, Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).SendAsync(111L, Echo.OnlyPhotosSupported, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("image/heic")]
    [InlineData("image/heif")]
    [InlineData("image/tiff")]
    public async Task An_unsupported_image_type_is_rejected_with_a_clear_reply_and_nothing_is_captured(string mimeType)
    {
        // M-7 (2026-09-25 final review): the router used to accept any image/* document, then
        // TelegramReceiptPhotoSource guessed the media type from the file extension and defaulted to
        // image/jpeg for anything it did not recognise - so a HEIC upload passed the router, SkiaSharp
        // could not decode it, and vision received bytes mislabelled as JPEG. The router now only
        // accepts the types TelegramReceiptPhotoSource.MediaTypeFor can correctly derive.
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(DocumentMessage(111L, 6, "doc-1", mimeType), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceiveWithAnyArgs().CaptureReceiptAsync(default!, default!, Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).SendAsync(111L, Echo.OnlyPhotosSupported, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    [InlineData("image/gif")]
    public async Task A_document_in_a_media_type_the_photo_source_can_decode_is_captured(string mimeType)
    {
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        captureStore.CaptureReceiptAsync(Arg.Any<CapturedReceipt>(), "Europe/Belgrade", Arg.Any<CancellationToken>()).Returns(transactionId);

        await router.HandleAsync(DocumentMessage(111L, 6, "doc-1", mimeType), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureReceiptAsync(
            Arg.Is<CapturedReceipt>(r => r.TelegramFileId == "doc-1"), "Europe/Belgrade", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Text_containing_a_fiscal_qr_link_anywhere_is_captured_as_a_receipt_not_text()
    {
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        captureStore.CaptureReceiptAsync(Arg.Any<CapturedReceipt>(), "Europe/Belgrade", Arg.Any<CancellationToken>()).Returns(transactionId);
        chatNotifier.SendAsync(111L, Echo.ReadingReceipt, Arg.Any<CancellationToken>()).Returns(779);
        const string text = "lunch https://suf.purs.gov.rs/v/?vl=AbCdEf123 thanks";

        await router.HandleAsync(TextMessage(111L, 7, text, DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureReceiptAsync(
            Arg.Is<CapturedReceipt>(r => r.VerificationUrl == "https://suf.purs.gov.rs/v/?vl=AbCdEf123" && r.TelegramFileId == null
                && r.Caption == text),
            "Europe/Belgrade", Arg.Any<CancellationToken>());
        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Plain_text_with_no_link_is_still_captured_as_text()
    {
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);
        captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        chatNotifier.SendAsync(111L, Echo.Acknowledgement, Arg.Any<CancellationToken>()).Returns(780);

        await router.HandleAsync(TextMessage(111L, 8, "coffee 250", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>());
        await captureStore.DidNotReceiveWithAnyArgs().CaptureReceiptAsync(default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_strangers_photo_is_ignored_before_capturing_anything()
    {
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(PhotoMessage(999L, 5, "photo-1"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceiveWithAnyArgs().CaptureReceiptAsync(default!, default!, Arg.Any<CancellationToken>());
        await chatNotifier.DidNotReceiveWithAnyArgs().SendAsync(default, default!, Arg.Any<CancellationToken>());
    }

    static Update StickerMessage(long chatId, int messageId) => new()
    {
        Id = 912,
        Message = new Message
        {
            Id = messageId,
            Chat = new Chat { Id = chatId },
            Date = DateTime.UtcNow,
            Sticker = new Sticker { FileId = "sticker-1", FileUniqueId = "sticker-unique-1", Width = 512, Height = 512, Type = StickerType.Regular },
        },
    };

    [Fact]
    public async Task A_sticker_from_the_owner_is_logged_and_answered_with_what_the_bot_can_read()
    {
        var (router, captureStore, chatNotifier, _, _, logger, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(StickerMessage(111L, 5), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default!, Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).SendAsync(111L, Echo.UnsupportedMessageType, Arg.Any<CancellationToken>());
        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == 6002).Subject;
        entry.Properties["MessageType"].Should().Be(MessageType.Sticker);
    }

    [Fact]
    public async Task A_strangers_sticker_produces_no_reply_and_no_log_event()
    {
        var (router, captureStore, chatNotifier, _, _, logger, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(StickerMessage(999L, 5), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default!, Arg.Any<CancellationToken>());
        await chatNotifier.DidNotReceiveWithAnyArgs().SendAsync(default, default!, Arg.Any<CancellationToken>());
        logger.Entries.Should().NotContain(e => e.EventId.Id == 6002);
    }

    [Fact]
    public async Task Text_that_merely_starts_with_health_but_is_not_the_exact_command_is_still_captured()
    {
        var (router, captureStore, chatNotifier, _, _, _, _) = CreateRouter(ownerChatId: 111L);
        captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        chatNotifier.SendAsync(111L, Echo.Acknowledgement, Arg.Any<CancellationToken>()).Returns(777);

        await router.HandleAsync(TextMessage(111L, 5, "/healthclub 500", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureAsync(
            Arg.Is<CapturedMessage>(m => m.Text == "/healthclub 500"), "Europe/Belgrade", Arg.Any<CancellationToken>());
    }
}
