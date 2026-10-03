namespace Noof.Ledger.Application.Chat;

public interface IChatNotifier
{
    Task<int> SendAsync(long chatId, string text, CancellationToken cancellationToken);

    Task EditAsync(long chatId, int messageId, EchoMessage message, CancellationToken cancellationToken);

    Task<int> AskAsync(long chatId, int replyToMessageId, string prompt, CancellationToken cancellationToken);

    // A reply quoting replyToMessageId that is sent even when that message no longer exists, with one "Close report"
    // button when closeReportNumber is given. Returns the sent message's id.
    Task<int> ReplyToBugReportAsync(
        long chatId, int replyToMessageId, string text, int? closeReportNumber, CancellationToken cancellationToken);

    Task AnswerActionAsync(string actionId, CancellationToken cancellationToken);
}
