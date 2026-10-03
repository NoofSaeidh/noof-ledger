using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Editing;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

// IsOwnerAsync, never IsAllowedAsync: neither /bug nor its button may claim an unowned bot, and a stranger gets silence.
internal sealed class BugCommandHandler(
    TelegramOwnerGate ownerGate, IRecordEditor editor, IBugReportStore store, IChatNotifier chatNotifier,
    ILogger<BugCommandHandler> logger)
{
    public static string SavedText(int number) => $"Bug report #{number} saved.";

    public static string ClosedLine(int number) => $"Bug report #{number} closed.";

    public async Task HandleAsync(Message message, string? reportText, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        if (!await ownerGate.IsOwnerAsync(chatId, cancellationToken))
        {
            logger.BugCommandRejected();
            return;
        }

        var transactionId = message.ReplyToMessage is { } repliedTo
            ? await FindRecordAsync(chatId, repliedTo.Id, cancellationToken)
            : null;

        // Saved before the reply: a failed send fails the update, and its redelivery must find this report, not file a
        // second one.
        var saved = await store.SaveFromTelegramAsync(
            new TelegramBugReport(chatId, message.Id, reportText, transactionId), cancellationToken);
        logger.BugReportSavedFromTelegram(saved.Number, transactionId is not null, saved.Created);

        await chatNotifier.ReplyToBugReportAsync(chatId, message.Id, SavedText(saved.Number), null, cancellationToken);
    }

    public async Task HandleCloseAsync(CallbackQuery query, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        if (!await ownerGate.IsOwnerAsync(chatId, cancellationToken))
        {
            logger.BugReportButtonRejected();
            return;
        }

        await chatNotifier.AnswerActionAsync(query.Id, cancellationToken);

        if (!BugReportButtons.TryParseClose(query.Data, out var number)
            || !await store.SetStatusAsync(number, BugReportStatus.Closed, cancellationToken))
            return;

        logger.BugReportClosedFromTelegram(number);
        var closedText = message.Text is { } shown ? $"{shown}\n\n{ClosedLine(number)}" : ClosedLine(number);
        await chatNotifier.EditAsync(chatId, message.Id, new EchoMessage(closedText, []), cancellationToken);
    }

    // The echo or Edit prompt it replies to, else the operator's own message that captured the record.
    async Task<Guid?> FindRecordAsync(long chatId, int repliedToId, CancellationToken cancellationToken)
    {
        var target = await editor.FindByBotMessageAsync(chatId, repliedToId, cancellationToken)
            ?? await editor.FindByUserMessageAsync(chatId, repliedToId, cancellationToken);
        return target?.TransactionId;
    }
}
