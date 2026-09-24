namespace Noof.Ledger.Application.Diagnostics;

// The trace reader (EfTransactionTrace) and every stage writer (Task 4/Telegram/Host workers) read these
// names and ids from here alone, so a stage can never drift between what is written and what is read back.
public static class TransactionStages
{
    public const string TransactionIdProperty = "TransactionId";
    public const string StageProperty = "Stage";

    public const string Received = "Received";
    public const int ReceivedEventId = 5001;

    public const string Transcribed = "Transcribed";
    public const int TranscribedEventId = 5002;

    public const string Categorized = "Categorized";
    public const int CategorizedEventId = 5003;

    public const string Persisted = "Persisted";
    public const int PersistedEventId = 5004;

    public const string Replied = "Replied";
    public const int RepliedEventId = 5005;

    public const string StageFailed = "StageFailed";
    public const int StageFailedEventId = 5009;

    public static IReadOnlyList<string> Ordered { get; } = [Received, Transcribed, Categorized, Persisted, Replied];
}
