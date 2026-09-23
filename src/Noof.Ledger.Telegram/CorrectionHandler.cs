using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Editing;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

internal sealed class CorrectionHandler(IRecordEditor editor, IChatNotifier chatNotifier)
{
    static readonly EchoMessage Correcting = new(RecordEcho.Correcting, []);

    // A reply to anything other than a record's echo or its Изменить prompt is not a correction: returning
    // false lets the router capture it as a new message.
    public async Task<bool> TryHandleReplyAsync(Message reply, Message repliedTo, string instruction, CancellationToken cancellationToken)
    {
        if (await editor.FindByBotMessageAsync(reply.Chat.Id, repliedTo.Id, cancellationToken) is not { } target)
            return false;

        if (await editor.RequestCorrectionAsync(target.TransactionId, instruction, reply.Id, cancellationToken)
            && target.EchoMessageId is { } echoId)
            await chatNotifier.EditAsync(reply.Chat.Id, echoId, Correcting, cancellationToken);

        return true;
    }

    public async Task HandleEditAsync(Message edited, CancellationToken cancellationToken)
    {
        if (edited.Text is not { Length: > 0 } text)
            return;

        if (await editor.FindByUserMessageAsync(edited.Chat.Id, edited.Id, cancellationToken) is not { } target)
            return;

        if (await editor.ReplaceRawTextAsync(target.TransactionId, text, cancellationToken)
            && target.EchoMessageId is { } echoId)
            await chatNotifier.EditAsync(edited.Chat.Id, echoId, Correcting, cancellationToken);
    }
}
