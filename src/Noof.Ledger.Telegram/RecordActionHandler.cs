using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Editing;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

internal sealed class RecordActionHandler(IRecordEditor editor, ICategorizationStore store, IChatNotifier chatNotifier)
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
            default:
                return;
        }

        // Refreshed even when nothing changed: an earlier attempt may have changed the record and then failed
        // to edit the echo. An identical edit is refused by Telegram and swallowed by the notifier.
        if (await store.GetSubjectAsync(target.TransactionId, cancellationToken) is { } record)
            await chatNotifier.EditAsync(echo.Chat.Id, echo.Id, RecordEcho.Compose(record), cancellationToken);
    }
}
