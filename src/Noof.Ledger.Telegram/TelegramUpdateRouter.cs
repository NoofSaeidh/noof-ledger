using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Domain;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramUpdateRouter(
    ICaptureStore captureStore,
    IChatNotifier chatNotifier,
    TelegramOwnerGate ownerGate,
    RecordActionHandler actionHandler,
    CorrectionHandler correctionHandler,
    IRecordEcho recordEcho,
    ISystemHealth systemHealth,
    ILogger<TelegramUpdateRouter> logger)
    : ITelegramUpdateRouter
{
    public async Task HandleAsync(Update update, string timeZoneId, CancellationToken cancellationToken)
    {
        switch (update)
        {
            case { Message: { } message }:
                await HandleMessageAsync(message, timeZoneId, cancellationToken);
                break;
            case { EditedMessage: { } edited }:
                if (await ownerGate.IsAllowedAsync(edited.Chat.Id, cancellationToken))
                    await correctionHandler.HandleEditAsync(edited, cancellationToken);
                break;
            case { CallbackQuery: { Message: { } echo } query }:
                // Rejected before reading Data, for the reason messages are rejected before reading Text.
                if (await ownerGate.IsAllowedAsync(echo.Chat.Id, cancellationToken))
                    await actionHandler.HandleAsync(query, echo, cancellationToken);
                break;
        }
    }

    async Task HandleMessageAsync(Message message, string timeZoneId, CancellationToken cancellationToken)
    {
        // /health is a fixed, known literal, not the free-form content the rule just below protects:
        // recognising it costs a stranger nothing, and IsOwnerAsync never claims ownership or tells
        // a non-owner anything back - unlike the capture path, which must not even look at the text
        // of someone who might not be the owner.
        if (message.Text is { Length: > 0 } possibleCommand && IsHealthCommand(possibleCommand))
        {
            await HandleHealthCommandAsync(message.Chat.Id, cancellationToken);
            return;
        }

        // Reject before reading Text: a stranger's content must never be inspected, not even to
        // decide whether it looks like a spend.
        if (!await ownerGate.IsAllowedAsync(message.Chat.Id, cancellationToken))
            return;

        if (message.Photo is { Length: > 0 } photos)
        {
            await HandleReceiptPhotoAsync(message, photos[^1].FileId, timeZoneId, cancellationToken);
            return;
        }

        if (message.Document is { } document)
        {
            await HandleReceiptDocumentAsync(message, document, timeZoneId, cancellationToken);
            return;
        }

        if (message.Voice is { } voice)
        {
            await HandleVoiceAsync(message, voice, timeZoneId, cancellationToken);
            return;
        }

        if (message.Text is not { Length: > 0 } text)
            return;

        if (message.ReplyToMessage is { } repliedTo
            && await correctionHandler.TryHandleReplyAsync(message, repliedTo, text, cancellationToken))
            return;

        if (ReceiptLinkDetector.TryFind(text, out var verificationUrl))
        {
            await HandleReceiptLinkAsync(message, text, verificationUrl, timeZoneId, cancellationToken);
            return;
        }

        // message.Date is when Telegram received it from the sender, not when we got around to
        // processing it -- an outage can queue a message for hours, and every queued message must
        // keep its own moment so time_zone_id buckets it into the correct local day later.
        // Message.Date deserialises as DateTime with Kind=Utc (confirmed against Telegram.Bot
        // 22.10.3.1's UnixDateTimeConverter), so this offset is genuinely zero, not just labelled so.
        var sentAt = new DateTimeOffset(message.Date);
        var captured = new CapturedMessage(message.Chat.Id, message.Id, text, sentAt);
        var transactionId = await captureStore.CaptureAsync(captured, timeZoneId, cancellationToken);

        using var scope = TransactionLogScope.Begin(logger, transactionId);
        logger.LogReceived(TransactionStages.Received, CaptureKind.Text, message.Chat.Id);

        await SendAcknowledgementAsync(message.Chat.Id, transactionId, recordEcho.Acknowledgement, cancellationToken);
    }

    async Task HandleVoiceAsync(Message message, Voice voice, string timeZoneId, CancellationToken cancellationToken)
    {
        if (message.ReplyToMessage is { } repliedTo
            && await correctionHandler.TryHandleVoiceReplyAsync(message, repliedTo, voice, cancellationToken))
            return;

        // message.Date is the send instant, Kind=Utc - see HandleMessageAsync.
        var captured = new CapturedVoice(message.Chat.Id, message.Id, voice.FileId, voice.Duration, new DateTimeOffset(message.Date));
        var transactionId = await captureStore.CaptureVoiceAsync(captured, timeZoneId, cancellationToken);

        using var scope = TransactionLogScope.Begin(logger, transactionId);
        logger.LogReceived(TransactionStages.Received, CaptureKind.Voice, message.Chat.Id);

        await SendAcknowledgementAsync(message.Chat.Id, transactionId, recordEcho.Transcribing, cancellationToken);
    }

    async Task HandleReceiptPhotoAsync(Message message, string fileId, string timeZoneId, CancellationToken cancellationToken)
    {
        var captured = new CapturedReceipt(
            message.Chat.Id, message.Id, new DateTimeOffset(message.Date), message.Caption, fileId, VerificationUrl: null);
        var transactionId = await captureStore.CaptureReceiptAsync(captured, timeZoneId, cancellationToken);

        using var scope = TransactionLogScope.Begin(logger, transactionId);
        logger.LogReceived(TransactionStages.Received, CaptureKind.Photo, message.Chat.Id);

        await SendAcknowledgementAsync(message.Chat.Id, transactionId, recordEcho.ReadingReceipt, cancellationToken);
    }

    async Task HandleReceiptDocumentAsync(Message message, Document document, string timeZoneId, CancellationToken cancellationToken)
    {
        if (document.MimeType is not { } mimeType || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            await chatNotifier.SendAsync(message.Chat.Id, recordEcho.OnlyPhotosSupported, cancellationToken);
            return;
        }

        await HandleReceiptPhotoAsync(message, document.FileId, timeZoneId, cancellationToken);
    }

    async Task HandleReceiptLinkAsync(Message message, string text, string verificationUrl, string timeZoneId, CancellationToken cancellationToken)
    {
        var captured = new CapturedReceipt(
            message.Chat.Id, message.Id, new DateTimeOffset(message.Date), text, TelegramFileId: null, verificationUrl);
        var transactionId = await captureStore.CaptureReceiptAsync(captured, timeZoneId, cancellationToken);

        using var scope = TransactionLogScope.Begin(logger, transactionId);
        logger.LogReceived(TransactionStages.Received, CaptureKind.Photo, message.Chat.Id);

        await SendAcknowledgementAsync(message.Chat.Id, transactionId, recordEcho.ReadingReceipt, cancellationToken);
    }

    async Task SendAcknowledgementAsync(long chatId, Guid transactionId, string text, CancellationToken cancellationToken)
    {
        try
        {
            var botMessageId = await chatNotifier.SendAsync(chatId, text, cancellationToken);
            await captureStore.AttachBotMessageAsync(transactionId, botMessageId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogStageFailed(TransactionStages.StageFailed, TransactionStages.Received, ex);
            throw;
        }
    }

    async Task HandleHealthCommandAsync(long chatId, CancellationToken cancellationToken)
    {
        if (!await ownerGate.IsOwnerAsync(chatId, cancellationToken))
        {
            logger.LogHealthCommandRejected();
            return;
        }

        var report = await systemHealth.GetAsync(fresh: true, cancellationToken);
        await chatNotifier.SendAsync(chatId, HealthReplyFormatter.Format(report), cancellationToken);
    }

    static bool IsHealthCommand(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("/health@", StringComparison.OrdinalIgnoreCase);
    }
}
