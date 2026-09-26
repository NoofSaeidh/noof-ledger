using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Receipts;
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
            default:
                return;
        }

        // Refreshed even when nothing changed: an earlier attempt may have changed the record and then failed
        // to edit the echo. An identical edit is refused by Telegram and swallowed by the notifier.
        if (await store.GetSubjectAsync(target.TransactionId, cancellationToken) is not { } record)
            return;

        // M-3 (Phase 6 final review): Cancel/Restore must keep the shop header and the receipt's own
        // line order, not fall back to the generic echo just because the button, not a worker, is
        // what re-renders it.
        var message = await receiptStore.GetByTransactionAsync(target.TransactionId, cancellationToken) is { } receipt
            ? recordEcho.ComposeReceipt(record, receipt)
            : recordEcho.Compose(record);
        await chatNotifier.EditAsync(echo.Chat.Id, echo.Id, message, cancellationToken);
    }
}
