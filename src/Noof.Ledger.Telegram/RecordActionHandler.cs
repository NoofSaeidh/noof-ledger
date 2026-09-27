using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

internal sealed class RecordActionHandler(
    IRecordEditor editor, ICategorizationStore store, IChatNotifier chatNotifier, IRecordEcho recordEcho,
    IReceiptStore receiptStore)
{
    public async Task HandleAsync(CallbackQuery query, Message echo, CancellationToken cancellationToken)
    {
        await chatNotifier.AnswerActionAsync(query.Id, cancellationToken);

        if (!RecordActionButtons.TryParse(query.Data, out var action))
            return;

        if (await editor.FindByBotMessageAsync(echo.Chat.Id, echo.Id, cancellationToken) is not { } target)
            return;

        switch (action)
        {
            case RecordAction.Cancel:
                await editor.CancelAsync(target.TransactionId, cancellationToken);
                break;
            case RecordAction.Restore:
                await editor.RestoreAsync(target.TransactionId, cancellationToken);
                break;
            case RecordAction.Edit:
                var promptId = await chatNotifier.AskAsync(echo.Chat.Id, echo.Id, recordEcho.EditPrompt, cancellationToken);
                await editor.AttachPromptAsync(target.TransactionId, promptId, cancellationToken);
                return;
            case RecordAction.RecordAnyway:
                // A stale press: the record was Cancelled (or already confirmed) between this button
                // being shown and being pressed. Enqueueing anyway would run CategorizeReceipt on a
                // Cancelled record - EfCategorizationStore.ApplyAsync keeps it Cancelled but still
                // writes line items, so the next Restore would land on a Captured record that already
                // has lines and no job, which ComposeReceipt renders as a dead-end "Reading the
                // receipt…" with no buttons. The guard against that is inside EnqueueCategorizationAsync
                // itself, atomic with the insert under the same row lock a concurrent Cancel takes
                // (2026-09-27) - checking it here first, separately, would leave the same gap it closes:
                // a Cancel landing between this check and the insert. Falling through to the refresh
                // below re-renders whatever the record's own current state actually is, whether or not
                // this queued anything.
                await receiptStore.EnqueueCategorizationAsync(target.TransactionId, echo.Id, cancellationToken);
                break;
            default:
                return;
        }

        // Refreshed even when nothing changed: an earlier attempt may have changed the record and then failed
        // to edit the echo. An identical edit is refused by Telegram and swallowed by the notifier.
        if (await store.GetSubjectAsync(target.TransactionId, cancellationToken) is not { } record)
            return;

        var receipt = await receiptStore.GetByTransactionAsync(target.TransactionId, cancellationToken);
        var message = await ComposeRefreshAsync(action, record, receipt, cancellationToken);
        await chatNotifier.EditAsync(echo.Chat.Id, echo.Id, message, cancellationToken);
    }

    // M-3 (Phase 6 final review): Cancel/Restore must keep the shop header and the receipt's own line
    // order, not fall back to the generic echo just because the button, not a worker, is what
    // re-renders it. 2026-09-27: a vision receipt with no CategorizeReceipt job yet (never confirmed,
    // or just Cancelled before it was) has no categorised line items to show - ComposeReceipt's generic
    // rendering reads record.Lines, which is empty, producing a false mismatch warning over an empty
    // total (Cancel) or a dead-end "Reading the receipt…" with no buttons (Restore). Both are routed to
    // the confirmation-aware echoes instead, and Record anyway's own refresh names the job that is now
    // actually running rather than the receipt's original Captured wording.
    async Task<EchoMessage> ComposeRefreshAsync(
        RecordAction action, CategorizationSubject record, ReceiptView? receipt, CancellationToken cancellationToken)
    {
        if (receipt is null)
            return recordEcho.Compose(record);

        if (await receiptStore.IsAwaitingConfirmationAsync(record.TransactionId, cancellationToken))
        {
            if (action != RecordAction.Restore)
                return recordEcho.ComposeReceiptCancelledUnconfirmed(record, receipt);

            return recordEcho.ComposeReceiptNeedsConfirmation(receipt);
        }

        // Only a Captured record has a CategorizeReceipt job running; a stale press on a Completed or
        // Failed receipt must keep its final echo and buttons.
        if (action == RecordAction.RecordAnyway && record.Status == TransactionStatus.Captured)
            return new EchoMessage(recordEcho.ComposeCategorisingReceipt(receipt.Lines.Count), []);

        return recordEcho.ComposeReceipt(record, receipt);
    }
}
