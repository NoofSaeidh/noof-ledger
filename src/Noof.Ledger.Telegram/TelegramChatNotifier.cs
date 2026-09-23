using Noof.Ledger.Application.Chat;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramChatNotifier(TelegramClientHandle clientHandle) : IChatNotifier
{
    public async Task<int> SendAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var message = await Client().SendMessage(chatId, text, cancellationToken: cancellationToken);
        return message.Id;
    }

    public async Task EditAsync(long chatId, int messageId, EchoMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await Client().EditMessageText(chatId, messageId, message.Text,
                replyMarkup: Keyboard(message.Actions), cancellationToken: cancellationToken);
        }
        // Telegram refuses an edit that changes nothing: a second tap on a button whose first tap already
        // produced this exact text. The chat already shows what it should.
        catch (ApiRequestException exception) when (exception.Message.Contains("message is not modified", StringComparison.Ordinal))
        {
        }
    }

    public async Task<int> AskAsync(long chatId, int replyToMessageId, string prompt, CancellationToken cancellationToken)
    {
        var message = await Client().SendMessage(chatId, prompt,
            replyParameters: new ReplyParameters { MessageId = replyToMessageId },
            replyMarkup: new ForceReplyMarkup { InputFieldPlaceholder = "no, 1500" },
            cancellationToken: cancellationToken);
        return message.Id;
    }

    public async Task AnswerActionAsync(string actionId, CancellationToken cancellationToken)
    {
        try
        {
            await Client().AnswerCallbackQuery(actionId, cancellationToken: cancellationToken);
        }
        // Answering only stops the button's spinner. A press handled after the host was down gets "query is
        // too old"; the press must still take effect, so the refusal is not allowed to fail the update.
        catch (ApiRequestException)
        {
        }
    }

    static InlineKeyboardMarkup? Keyboard(IReadOnlyList<RecordAction> actions) =>
        actions.Count == 0 ? null : new InlineKeyboardMarkup(actions.Select(RecordActionButtons.ToButton));

    ITelegramBotClient Client() =>
        clientHandle.Current ?? throw new InvalidOperationException(
            "The Telegram client is not ready yet: no bot token has been saved.");
}
