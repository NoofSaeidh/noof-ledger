namespace Noof.Ledger.Application.Reporting.Summary;

public sealed record SummaryExplanationJob(long ChatId, string SummaryText);

public interface ISummaryExplanationQueue
{
    // Never throws; false when 8 presses are already waiting (spec P-12).
    bool TryEnqueue(SummaryExplanationJob job);
}
