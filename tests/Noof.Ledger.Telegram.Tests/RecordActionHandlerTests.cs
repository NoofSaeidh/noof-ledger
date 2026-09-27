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

    sealed record Harness(RecordActionHandler Handler, IReceiptStore ReceiptStore, IChatNotifier Notifier);

    static Harness Create(TransactionStatus status = TransactionStatus.Captured)
    {
        var editor = Substitute.For<IRecordEditor>();
        editor.FindByBotMessageAsync(555L, 42, Arg.Any<CancellationToken>())
            .Returns(new EchoTarget(TransactionId, 42));

        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(new CategorizationSubject(
                TransactionId, "", 555L, 42, "Cash", status, new DateOnly(2026, 9, 27), new DateOnly(2026, 9, 27),
                [], CaptureKind.Photo));

        var receiptStore = Substitute.For<IReceiptStore>();
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns((ReceiptView?)null);
        receiptStore.EnqueueCategorizationAsync(TransactionId, 42, Arg.Any<CancellationToken>()).Returns(true);

        var notifier = Substitute.For<IChatNotifier>();

        var handler = new RecordActionHandler(editor, store, notifier, Echo, receiptStore);
        return new Harness(handler, receiptStore, notifier);
    }

    static CallbackQuery RecordAnyway() => new()
    {
        Id = "cb-1",
        Data = "record_anyway",
        Message = new Message { Id = 42, Chat = new Chat { Id = 555L } },
    };

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
