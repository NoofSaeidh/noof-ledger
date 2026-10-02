using NSubstitute;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

// RecordAction.RecordAnyway (2026-09-27): the operator's own say-so on a vision receipt
// ExtractReceiptWorker saved without enqueueing CategorizeReceipt.
public class RecordActionHandlerTests
{
    static readonly Guid TransactionId = Guid.NewGuid();
    static readonly IRecordEcho Echo = new RecordEcho();

    sealed record Harness(RecordActionHandler Handler, IReceiptStore ReceiptStore, IChatNotifier Notifier, IRecordEditor Editor);

    static Harness Create(TransactionStatus status = TransactionStatus.Captured)
    {
        var editor = Substitute.For<IRecordEditor>();
        editor.FindByBotMessageAsync(555L, 42, Arg.Any<CancellationToken>())
            .Returns(new EchoTarget(TransactionId, 42));
        editor.CancelAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(true);
        editor.RestoreAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(true);

        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(new CategorizationSubject(
                TransactionId, "", 555L, 42, "Cash", status, new DateOnly(2026, 9, 27), new DateOnly(2026, 9, 27),
                [], CaptureKind.Photo));

        var receiptStore = Substitute.For<IReceiptStore>();
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns((ReceiptView?)null);
        receiptStore.EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>()).Returns(true);
        // A RecordAnyway press only makes sense while the receipt is still awaiting confirmation
        // (EfReceiptStore.EnqueueCategorizationAsync's own guard checks this before enqueueing) - true
        // by default so the "happy path" tests below exercise a legitimate press; the
        // malformed-scenario test overrides it.
        receiptStore.IsAwaitingConfirmationAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(true);

        var notifier = Substitute.For<IChatNotifier>();

        var handler = new RecordActionHandler(editor, store, notifier, Echo, receiptStore);
        return new Harness(handler, receiptStore, notifier, editor);
    }

    static CallbackQuery RecordAnyway() => new()
    {
        Id = "cb-1",
        Data = "record_anyway",
        Message = new Message { Id = 42, Chat = new Chat { Id = 555L } },
    };

    static CallbackQuery Cancel() => new()
    {
        Id = "cb-1",
        Data = "cancel",
        Message = new Message { Id = 42, Chat = new Chat { Id = 555L } },
    };

    static CallbackQuery Restore() => new()
    {
        Id = "cb-1",
        Data = "restore",
        Message = new Message { Id = 42, Chat = new Chat { Id = 555L } },
    };

    static ReceiptView UnconfirmedVisionReceipt() => new(
        Guid.NewGuid(), ReceiptSource.Vision, null, "Test Market", null, null, null, null, 500m, CurrencyCode.Rsd,
        ReceiptKind.Sale, PaymentMethod.Card, null, null,
        [new ReceiptLineView(Guid.NewGuid(), 1, "Bread", 1m, null, 123.46m, 123.46m, null)]);

    [Fact]
    public async Task Record_anyway_enqueues_categorisation_with_the_echo_message_id_as_the_source()
    {
        var harness = Create();
        var query = RecordAnyway();

        await harness.Handler.HandleAsync(query, query.Message!, TestContext.Current.CancellationToken);

        await harness.ReceiptStore.Received(1).EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Record_anyway_answers_the_callback_and_refreshes_the_echo()
    {
        var harness = Create();
        var query = RecordAnyway();

        await harness.Handler.HandleAsync(query, query.Message!, TestContext.Current.CancellationToken);

        await harness.Notifier.Received(1).AnswerActionAsync("cb-1", Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_second_press_is_a_no_op_but_still_refreshes_the_echo()
    {
        var harness = Create();
        harness.ReceiptStore.EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>()).Returns(false);
        var query = RecordAnyway();

        await harness.Handler.HandleAsync(query, query.Message!, TestContext.Current.CancellationToken);

        await harness.ReceiptStore.Received(1).EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }

    // R2-4-style dead end (2026-09-27 finding): Cancel on a vision receipt still awaiting confirmation
    // used to fall back to the generic Cancelled rendering, which reads categorised line items that
    // were never written (CategorizeReceipt never ran) - a false "Lines add up to 0.00" warning over an
    // empty total.
    [Fact]
    public async Task Cancel_on_an_unconfirmed_vision_receipt_shows_the_receipts_own_total_not_a_false_mismatch()
    {
        var harness = Create();
        var receipt = UnconfirmedVisionReceipt();
        harness.ReceiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(receipt);
        harness.ReceiptStore.IsAwaitingConfirmationAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(true);

        await harness.Handler.HandleAsync(Cancel(), Cancel().Message!, TestContext.Current.CancellationToken);

        await harness.Editor.Received(1).CancelAsync(TransactionId, Arg.Any<CancellationToken>());
        var expectedRecord = new CategorizationSubject(TransactionId, "", 555L, 42, "Cash", TransactionStatus.Captured,
            new DateOnly(2026, 9, 27), new DateOnly(2026, 9, 27), Array.Empty<RecordedLine>(), CaptureKind.Photo);
        var expectedText = Echo.ComposeReceiptCancelledUnconfirmed(expectedRecord, receipt).Text;
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
            m.Text == expectedText && m.Actions.SequenceEqual(new[] { RecordAction.Restore })),
            Arg.Any<CancellationToken>());
    }

    // The other half of the same dead end: Restore used to land back on ComposeReceipt's Captured
    // branch ("Reading the receipt…", no buttons) - nothing is reading it, and both RecordAnyway and
    // Cancel were gone for good.
    [Fact]
    public async Task Restore_on_an_unconfirmed_vision_receipt_re_offers_record_anyway_and_cancel()
    {
        var harness = Create(status: TransactionStatus.Cancelled);
        var receipt = UnconfirmedVisionReceipt();
        harness.ReceiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(receipt);
        harness.ReceiptStore.IsAwaitingConfirmationAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(true);

        await harness.Handler.HandleAsync(Restore(), Restore().Message!, TestContext.Current.CancellationToken);

        await harness.Editor.Received(1).RestoreAsync(TransactionId, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
            m.Actions.SequenceEqual(new[] { RecordAction.RecordAnyway, RecordAction.Cancel })),
            Arg.Any<CancellationToken>());
    }

    // 2026-09-27 finding: the interim render right after Record anyway showed "Reading the receipt…"
    // (ComposeReceipt's generic Captured branch) rather than naming the job that is now actually
    // running.
    [Fact]
    public async Task Record_anyway_renders_the_categorising_text_not_reading_the_receipt()
    {
        var harness = Create();
        var receipt = UnconfirmedVisionReceipt();
        harness.ReceiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(receipt);
        harness.ReceiptStore.IsAwaitingConfirmationAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(false);
        var query = RecordAnyway();

        await harness.Handler.HandleAsync(query, query.Message!, TestContext.Current.CancellationToken);

        await harness.Notifier.Received(1).EditAsync(555L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ComposeCategorisingReceipt(receipt.Lines.Count) && m.Actions.Count == 0),
            Arg.Any<CancellationToken>());
    }

    // 2026-09-27 finding: a stale RecordAnyway press delivered after Cancel used to be filtered out by
    // a status read the handler did itself before enqueueing, which was not atomic with a concurrent
    // Cancel - EfCategorizationStore.ApplyAsync keeps a Cancelled record Cancelled but still writes its
    // line items, so the next Restore landed on a Captured record that already had lines and no job:
    // ComposeReceipt's dead-end "Reading the receipt…" with no buttons. The atomic guard now lives
    // inside EfReceiptStore.EnqueueCategorizationAsync itself: the handler always calls it and treats
    // a false result - the store's own row-locked status check failing - the same way it always
    // treated "already queued".
    [Fact]
    public async Task A_stale_record_anyway_press_after_cancel_does_not_enqueue_and_re_renders_the_cancelled_echo()
    {
        var harness = Create(status: TransactionStatus.Cancelled);
        var receipt = UnconfirmedVisionReceipt();
        harness.ReceiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(receipt);
        harness.ReceiptStore.IsAwaitingConfirmationAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(true);
        harness.ReceiptStore.EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>()).Returns(false);
        var query = RecordAnyway();

        await harness.Handler.HandleAsync(query, query.Message!, TestContext.Current.CancellationToken);

        await harness.ReceiptStore.Received(1).EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>());
        var expectedRecord = new CategorizationSubject(TransactionId, "", 555L, 42, "Cash", TransactionStatus.Cancelled,
            new DateOnly(2026, 9, 27), new DateOnly(2026, 9, 27), Array.Empty<RecordedLine>(), CaptureKind.Photo);
        var expectedText = Echo.ComposeReceiptCancelledUnconfirmed(expectedRecord, receipt).Text;
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
            m.Text == expectedText && m.Actions.SequenceEqual(new[] { RecordAction.Restore })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stale_record_anyway_press_after_the_receipt_was_recorded_keeps_the_recorded_echo()
    {
        var harness = Create(status: TransactionStatus.Completed);
        var receipt = UnconfirmedVisionReceipt();
        harness.ReceiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(receipt);
        harness.ReceiptStore.IsAwaitingConfirmationAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(false);
        harness.ReceiptStore.EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>()).Returns(false);
        var query = RecordAnyway();

        await harness.Handler.HandleAsync(query, query.Message!, TestContext.Current.CancellationToken);

        await harness.ReceiptStore.Received(1).EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>());
        var expectedRecord = new CategorizationSubject(TransactionId, "", 555L, 42, "Cash", TransactionStatus.Completed,
            new DateOnly(2026, 9, 27), new DateOnly(2026, 9, 27), Array.Empty<RecordedLine>(), CaptureKind.Photo);
        var expected = Echo.ComposeReceipt(expectedRecord, receipt);
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
            m.Text == expected.Text && m.Actions.SequenceEqual(expected.Actions)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_target_message_does_nothing()
    {
        var editor = Substitute.For<IRecordEditor>();
        editor.FindByBotMessageAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);
        var receiptStore = Substitute.For<IReceiptStore>();
        var handler = new RecordActionHandler(
            editor, Substitute.For<ICategorizationStore>(), Substitute.For<IChatNotifier>(), Echo, receiptStore);
        var query = RecordAnyway();

        await handler.HandleAsync(query, query.Message!, TestContext.Current.CancellationToken);

        await receiptStore.DidNotReceiveWithAnyArgs().EnqueueCategorizationAsync(default, default, Arg.Any<CancellationToken>());
    }

    static readonly ExtractedExchange HeldEvidence = new(100.00m, "EUR", 11650.00m, "RSD", 117.0000m, null, null, "PZ-2026-0917");

    static ExchangeSlipView HeldSlipView() => new(
        Guid.Parse("00000000-0000-0000-0007-000000000030"), null, "Menjačnica Zlatnik",
        new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero), "PZ-2026-0917", HeldEvidence);

    static ReceiptView SlipReceipt() => new(
        Guid.Parse("00000000-0000-0000-0007-000000000030"), ReceiptSource.Vision, null, "Menjačnica Zlatnik", null, null, null,
        new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero), 11650.00m, CurrencyCode.Rsd, ReceiptKind.Exchange, null, null, null, []);

    static CategorizationSubject SlipRecord(TransactionStatus status, RecordFailureReason failure = RecordFailureReason.None) =>
        new(TransactionId, "", 555L, 42, "", status, new DateOnly(2026, 9, 27), new DateOnly(2026, 9, 27), [], CaptureKind.Photo,
            FailureReason: failure, Slip: new SlipFacts("Menjačnica Zlatnik", "PZ-2026-0917", HeldEvidence));

    static CategorizationSubject RecordedSlip(TransactionStatus status) =>
        SlipRecord(status) with
        {
            Kind = TransactionKind.Transfer,
            WalletName = "Cash EUR",
            WalletCurrency = CurrencyCode.Eur,
            Transfer = new TransferView(
                Guid.Parse("00000000-0000-0000-0007-0000000000e1"), "Cash EUR", new Money(100.00m, CurrencyCode.Eur),
                Guid.Parse("00000000-0000-0000-0007-0000000000d1"), "Cash RSD", new Money(11650.00m, CurrencyCode.Rsd),
                null, null, new ExchangeRate(CurrencyCode.Eur, 117.0000m, CurrencyCode.Rsd), "Menjačnica Zlatnik", [], []),
        };

    static Harness CreateForSlip(CategorizationSubject record, bool awaiting)
    {
        var editor = Substitute.For<IRecordEditor>();
        editor.FindByBotMessageAsync(555L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(TransactionId, 42));
        editor.CancelAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(true);
        editor.RestoreAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(true);

        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(record);

        var receiptStore = Substitute.For<IReceiptStore>();
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(SlipReceipt());
        receiptStore.GetExchangeSlipAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(HeldSlipView());
        receiptStore.IsAwaitingConfirmationAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(awaiting);
        receiptStore.EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>()).Returns(true);

        var notifier = Substitute.For<IChatNotifier>();
        return new Harness(new RecordActionHandler(editor, store, notifier, Echo, receiptStore), receiptStore, notifier, editor);
    }

    // Restore of a held slip brings back its "Record anyway" prompt.
    [Fact]
    public async Task Restore_of_a_held_slip_shows_its_confirmation_prompt_again()
    {
        var harness = CreateForSlip(SlipRecord(TransactionStatus.Captured), awaiting: true);

        await harness.Handler.HandleAsync(Restore(), Restore().Message!, TestContext.Current.CancellationToken);

        await harness.Editor.Received(1).RestoreAsync(TransactionId, Arg.Any<CancellationToken>());
        var prompt = Echo.ComposeSlipNeedsConfirmation(HeldSlipView());
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
                m.Text == prompt.Text && m.Actions.SequenceEqual(new[] { RecordAction.RecordAnyway, RecordAction.Cancel })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_on_a_held_slip_says_it_was_never_recorded_and_offers_restore()
    {
        var harness = CreateForSlip(SlipRecord(TransactionStatus.Cancelled), awaiting: true);

        await harness.Handler.HandleAsync(Cancel(), Cancel().Message!, TestContext.Current.CancellationToken);

        var expected = Echo.ComposeSlipCancelledUnconfirmed(HeldSlipView());
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
                m.Text == expected.Text && m.Actions.SequenceEqual(new[] { RecordAction.Restore })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Record_anyway_on_a_held_slip_queues_it_and_says_the_exchange_is_being_recorded()
    {
        var harness = CreateForSlip(SlipRecord(TransactionStatus.Captured), awaiting: false);
        var query = RecordAnyway();

        await harness.Handler.HandleAsync(query, query.Message!, TestContext.Current.CancellationToken);

        await harness.ReceiptStore.Received(1).EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(555L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.RecordingExchange && m.Actions.Count == 0), Arg.Any<CancellationToken>());
    }

    // The store refuses Record anyway on a slip still held and still Captured (a reply to it in flight): the echo is
    // the record as it stands - the held prompt - never "Cancelled", which the record is not.
    [Fact]
    public async Task A_refused_record_anyway_on_a_held_slip_still_captured_shows_its_confirmation_prompt()
    {
        var harness = CreateForSlip(SlipRecord(TransactionStatus.Captured), awaiting: true);
        harness.ReceiptStore.EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>()).Returns(false);
        var query = RecordAnyway();

        await harness.Handler.HandleAsync(query, query.Message!, TestContext.Current.CancellationToken);

        var prompt = Echo.ComposeSlipNeedsConfirmation(HeldSlipView());
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
                m.Text == prompt.Text && m.Actions.SequenceEqual(new[] { RecordAction.RecordAnyway, RecordAction.Cancel })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stale_record_anyway_on_a_cancelled_held_slip_keeps_it_never_recorded()
    {
        var harness = CreateForSlip(SlipRecord(TransactionStatus.Cancelled), awaiting: true);
        harness.ReceiptStore.EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>()).Returns(false);
        var query = RecordAnyway();

        await harness.Handler.HandleAsync(query, query.Message!, TestContext.Current.CancellationToken);

        var expected = Echo.ComposeSlipCancelledUnconfirmed(HeldSlipView());
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
                m.Text == expected.Text && m.Actions.SequenceEqual(new[] { RecordAction.Restore })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_on_a_recorded_slip_renders_the_cancelled_exchange_never_a_shop_receipt()
    {
        var record = RecordedSlip(TransactionStatus.Cancelled);
        var harness = CreateForSlip(record, awaiting: false);

        await harness.Handler.HandleAsync(Cancel(), Cancel().Message!, TestContext.Current.CancellationToken);

        var expected = Echo.Compose(record);
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
                m.Text == expected.Text && !m.Text.Contains("Lines add up to") && m.Actions.SequenceEqual(new[] { RecordAction.Restore })),
            Arg.Any<CancellationToken>());
    }

    // An incomplete slip is never offered Record anyway: only a reply completes it.
    [Fact]
    public async Task Restore_of_an_incomplete_slip_is_never_offered_record_anyway()
    {
        var record = SlipRecord(TransactionStatus.Failed, RecordFailureReason.SlipIncomplete);
        var harness = CreateForSlip(record, awaiting: false);

        await harness.Handler.HandleAsync(Restore(), Restore().Message!, TestContext.Current.CancellationToken);

        var expected = Echo.Compose(record);
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
                m.Text == expected.Text && !m.Actions.Contains(RecordAction.RecordAnyway)),
            Arg.Any<CancellationToken>());
    }

    // A photo cancelled while it was read, then restored once its incomplete slip was saved, asks for the missing
    // figure with Edit - never "Reading the receipt…" with no buttons.
    [Fact]
    public async Task Restore_of_a_slip_saved_incomplete_while_cancelled_asks_for_the_missing_figure()
    {
        var record = SlipRecord(TransactionStatus.Failed, RecordFailureReason.SlipIncomplete) with
        {
            Slip = new SlipFacts("Menjačnica Zlatnik", "PZ-2026-0917", HeldEvidence with { ReceivedAmount = null, Rate = null }),
        };
        var harness = CreateForSlip(record, awaiting: false);

        await harness.Handler.HandleAsync(Restore(), Restore().Message!, TestContext.Current.CancellationToken);

        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
                m.Text == "Slip read, but the amount received is unreadable — reply with it."
                && m.Actions.SequenceEqual(new[] { RecordAction.Edit })),
            Arg.Any<CancellationToken>());
    }

    // A slip a reply completed, then cancelled, then restored, offers Cancel and Edit - never Record anyway.
    [Fact]
    public async Task Restore_of_a_slip_a_reply_completed_is_never_offered_record_anyway()
    {
        var record = RecordedSlip(TransactionStatus.Completed);
        var harness = CreateForSlip(record, awaiting: false);

        await harness.Handler.HandleAsync(Restore(), Restore().Message!, TestContext.Current.CancellationToken);

        var expected = Echo.Compose(record);
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
                m.Text == expected.Text && !m.Actions.Contains(RecordAction.RecordAnyway)),
            Arg.Any<CancellationToken>());
    }
}
