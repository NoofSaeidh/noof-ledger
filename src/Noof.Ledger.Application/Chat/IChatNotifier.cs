namespace Noof.Ledger.Application.Chat;

public interface IChatNotifier
{
    Task<int> SendAsync(long chatId, string text, CancellationToken cancellationToken);

    Task EditAsync(long chatId, int messageId, EchoMessage message, CancellationToken cancellationToken);

    Task AnswerActionAsync(string actionId, CancellationToken cancellationToken);
}
