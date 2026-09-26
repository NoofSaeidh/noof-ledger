namespace Noof.Ledger.Application.Diagnostics;

// The one place timed-operation names live, the same role TransactionStages plays for stages.
public static class TimedOperations
{
    public const string ModelCategorize = "model.categorize";
    public const string ModelRequest = "model.request";
    public const string ModelCanonicalize = "model.canonicalize";
    public const string ModelProbe = "model.probe";
    public const string SpeechProbe = "speech.probe";
    public const string SpeechTranscribe = "speech.transcribe";

    public const string TelegramGetUpdates = "telegram.getUpdates";
    public const string TelegramHandleUpdate = "telegram.handleUpdate";
    public const string TelegramSendMessage = "telegram.sendMessage";
    public const string TelegramEditMessage = "telegram.editMessage";
    public const string TelegramAskReply = "telegram.askReply";
    public const string TelegramAnswerCallback = "telegram.answerCallback";
    public const string TelegramDownloadFile = "telegram.downloadFile";

    public const string DbReleaseExpiredLeases = "db.releaseExpiredLeases";
    public const string DbClaimJob = "db.claimJob";
    public const string DbLoadCategorizationContext = "db.loadCategorizationContext";
    public const string DbApplyCategorization = "db.applyCategorization";
    public const string DbCompleteTranscription = "db.completeTranscription";
    public const string DbCompleteJob = "db.completeJob";

    public const string JobQueueWait = "job.queueWait";
    public const string JobCategorize = "job.categorize";
    public const string JobTranscribe = "job.transcribe";

    public const string BackupDump = "backup.dump";
    public const string LogsPrune = "logs.prune";
    public const string HealthRun = "health.run";

    public static string HealthCheck(string checkName) => $"health.{checkName}";
}
