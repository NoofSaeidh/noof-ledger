namespace Noof.Ledger.Application.Capture;

public sealed record CapturedMessage(long ChatId, int MessageId, string Text, DateTimeOffset SentAt);
