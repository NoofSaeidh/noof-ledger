using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Receipts;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

internal sealed class CorrectionHandler(
    IRecordEditor editor, IChatNotifier chatNotifier, IRecordEcho recordEcho, IReceiptStore receiptStore)
{
    readonly EchoMessage correcting = new(recordEcho.Correcting, []);
    readonly EchoMessage transcribing = new(recordEcho.Transcribing, []);

    // A reply to anything other than a record's echo or its Edit prompt is not a correction: returning
    // false lets the router capture it as a new message.
    public async Task<bool> TryHandleReplyAsync(Message reply, Message repliedTo, string instruction, CancellationToken cancellationToken)
    {
        if (await editor.FindByBotMessageAsync(reply.Chat.Id, repliedTo.Id, cancellationToken) is not { } target)
            return false;

        // reply.Date deserialises as DateTime with Kind=Utc, same as TelegramUpdateRouter's message.Date.
        var sentAt = new DateTimeOffset(reply.Date);
        if (await editor.RequestCorrectionAsync(target.TransactionId, instruction, reply.Id, sentAt, cancellationToken)
            && target.EchoMessageId is { } echoId)
            await chatNotifier.EditAsync(reply.Chat.Id, echoId, correcting, cancellationToken);

        return true;
    }

    // The spoken form of TryHandleReplyAsync: the reply's voice is queued for transcription and its transcript
    // becomes the correction (V7). False lets the router capture the note as a new record.
    public async Task<bool> TryHandleVoiceReplyAsync(Message reply, Message repliedTo, Voice voice, CancellationToken cancellationToken)
    {
        if (await editor.FindByBotMessageAsync(reply.Chat.Id, repliedTo.Id, cancellationToken) is not { } target)
            return false;

        // reply.Date deserialises as DateTime with Kind=Utc, same as TelegramUpdateRouter's message.Date.
        var sentAt = new DateTimeOffset(reply.Date);
        if (await editor.RequestVoiceCorrectionAsync(target.TransactionId, voice.FileId, reply.Id, sentAt, cancellationToken)
            && target.EchoMessageId is { } echoId)
            await chatNotifier.EditAsync(reply.Chat.Id, echoId, transcribing, cancellationToken);

        return true;
    }

    public async Task HandleEditAsync(Message edited, CancellationToken cancellationToken)
    {
        // R2-5 (Phase 6 second re-review): Telegram delivers an edited photo's or document's own free
        // text as Caption, never Text - dropping it here silently lost every receipt-photo caption
        // edit. A voice note can carry a Caption too, but its correction text is its transcript
        // (TryHandleVoiceReplyAsync), not a caption, so only a photo/document edit falls back to it.
        var text = edited.Text ?? (edited.Photo is not null || edited.Document is not null ? edited.Caption : null);
        if (text is not { Length: > 0 })
            return;

        if (await editor.FindByUserMessageAsync(edited.Chat.Id, edited.Id, cancellationToken) is not { } target)
            return;

        // N-2 (Phase 6 re-review): a link capture's own message can be edited into a DIFFERENT fiscal
        // receipt link - that is a new receipt, not a correction of this one, and letting it reach
        // Reinterpret's record_transaction re-read would silently wipe the first receipt's lines.
        // Anything else (a plain correction instruction, or the same link resent) is an ordinary edit,
        // handled by CategorizationWorker's own claim-time routing once it reaches Reinterpret.
        if (await receiptStore.GetVerificationUrlAsync(target.TransactionId, cancellationToken) is { Length: > 0 } originalLink
            && ReceiptLinkDetector.TryFind(text, out var editedLink)
            && !string.Equals(editedLink, originalLink, StringComparison.Ordinal))
        {
            // R2-2 (Phase 6 second re-review): a chatNotifier.EditAsync here would overwrite the
            // record's own echo - its "Recorded" summary and Cancel/Edit buttons - with this notice,
            // for a record the notice itself says is unchanged. A separate message keeps the echo intact.
            await chatNotifier.SendAsync(edited.Chat.Id, recordEcho.NewReceiptLinkMustBeSentSeparately.Text, cancellationToken);
            return;
        }

        if (await editor.ReplaceRawTextAsync(target.TransactionId, text, cancellationToken)
            && target.EchoMessageId is { } echoId)
            await chatNotifier.EditAsync(edited.Chat.Id, echoId, correcting, cancellationToken);
    }
}
