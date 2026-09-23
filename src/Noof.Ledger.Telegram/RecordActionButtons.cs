using Noof.Ledger.Application.Chat;
using Telegram.Bot.Types.ReplyMarkups;

namespace Noof.Ledger.Telegram;

// One table for the label a person sees and the data Telegram sends back, so the two can never drift.
internal static class RecordActionButtons
{
    static readonly (RecordAction Action, string Label, string Data)[] Buttons =
    [
        (RecordAction.Cancel, "Отменить", "cancel"),
        (RecordAction.Edit, "Изменить", "edit"),
        (RecordAction.Restore, "Вернуть", "restore"),
    ];

    public static InlineKeyboardButton ToButton(RecordAction action)
    {
        var button = Buttons.Single(candidate => candidate.Action == action);
        return InlineKeyboardButton.WithCallbackData(button.Label, button.Data);
    }
}
