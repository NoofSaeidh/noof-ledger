namespace Noof.Ledger.Domain;

public sealed class Transaction
{
    public required Guid Id { get; init; }
    public required Guid WalletId { get; init; }

    public required string RawText { get; init; }

    public required TransactionStatus Status { get; set; }

    // IANA id (e.g. "Europe/Belgrade"), stamped from Capture:TimeZone at capture.
    public required string TimeZoneId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
    public required long TelegramChatId { get; init; }

    // The user's own message.
    public required int TelegramMessageId { get; init; }

    // Our reply, attached after capture and edited as categorization finishes -
    // unlike TelegramMessageId, it starts unset.
    public int? BotMessageId { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
}
