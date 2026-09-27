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
        // (RecordActionHandler's own guard checks this before enqueueing) - true by default so the
        // "happy path" tests below exercise a legitimate press; the malformed-scenario test overrides it.
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

    // 2026-09-27 finding: a stale RecordAnyway press delivered after Cancel used to enqueue
    // CategorizeReceipt unconditionally - EfCategorizationStore.ApplyAsync keeps a Cancelled record
    // Cancelled but still writes its line items, so the next Restore landed on a Captured record that
    // already had lines and no job: ComposeReceipt's dead-end "Reading the receipt…" with no buttons.
    // The guard must check the record's own current status, not just whether the button was ever valid.
    [Fact]
    public async Task A_stale_record_anyway_press_after_cancel_does_not_enqueue_and_re_renders_the_cancelled_echo()
    {
        var harness = Create(status: TransactionStatus.Cancelled);
        var receipt = UnconfirmedVisionReceipt();
        harness.ReceiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(receipt);
        harness.ReceiptStore.IsAwaitingConfirmationAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(true);
        var query = RecordAnyway();

        await harness.Handler.HandleAsync(query, query.Message!, TestContext.Current.CancellationToken);

        await harness.ReceiptStore.DidNotReceiveWithAnyArgs().EnqueueCategorizationAsync(default, default, Arg.Any<CancellationToken>());
        var expectedRecord = new CategorizationSubject(TransactionId, "", 555L, 42, "Cash", TransactionStatus.Cancelled,
            new DateOnly(2026, 9, 27), new DateOnly(2026, 9, 27), Array.Empty<RecordedLine>(), CaptureKind.Photo);
        var expectedText = Echo.ComposeReceiptCancelledUnconfirmed(expectedRecord, receipt).Text;
        await harness.Notifier.Received(1).EditAsync(555L, 42, Arg.Is<EchoMessage>(m =>
            m.Text == expectedText && m.Actions.SequenceEqual(new[] { RecordAction.Restore })),
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
}
