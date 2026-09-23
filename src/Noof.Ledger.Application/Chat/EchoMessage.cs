namespace Noof.Ledger.Application.Chat;

public enum RecordAction
{
    Cancel,
    Edit,
    Restore,
}

public sealed record EchoMessage(string Text, IReadOnlyList<RecordAction> Actions);
