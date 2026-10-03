namespace Noof.Ledger.Application.Chat;

public interface IChatNotifier
{
    Task<int> SendAsync(long chatId, string text, CancellationToken cancellationToken);

    Task EditAsync(long chatId, int messageId, EchoMessage message, CancellationToken cancellationToken);

    Task<int> AskAsync(long chatId, int replyToMessageId, string prompt, CancellationToken cancellationToken);

    // A reply to a bug report's own message, sent even when that message no longer exists, with one "Close report"
    // button when closeReportNumber is given. replyTo and the returned reference to the sent reply are opaque outside
    // the chat's own layer (R-2).
    Task<string> ReplyToBugReportAsync(
        string replyTo, string text, int? closeReportNumber, CancellationToken cancellationToken);

    Task AnswerActionAsync(string actionId, CancellationToken cancellationToken);
}
