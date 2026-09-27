namespace Noof.Ledger.Application.Chat;

public enum RecordAction
{
    Cancel,
    Edit,
    Restore,

    // 2026-09-27: offered only on a vision-read receipt whose lines do not add up to its total, or
    // whose printed tax id is not exactly 9 digits - the operator's own say-so that the record is
    // right anyway, before CategorizeReceipt is ever enqueued.
    RecordAnyway,
}

public sealed record EchoMessage(string Text, IReadOnlyList<RecordAction> Actions);
