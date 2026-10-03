using Noof.Ledger.Application.Diagnostics.BugReports;

namespace Noof.Ledger.Host.Workers;

internal static class BugReportReplies
{
    public const string CouldNotExplain = "Couldn't explain it — the report is saved.";

    // Telegram refuses a message over 4096 characters, and a refused reply would be retried every tick for ever.
    public const int MaxExplanationLength = 3500;

    public static string Compose(BugReportDelivery delivery) => delivery switch
    {
        { State: BugExplanationState.Done, Explanation: { } explanation } => $"{Cut(explanation)}\n\n{FindingsLine(delivery)}",
        _ => CouldNotExplain,
    };

    public static string FindingsLine(BugReportDelivery delivery) => (delivery.FindingsCount, delivery.Linked) switch
    {
        (null, _) => "Integrity findings could not be collected",
        (0, _) => "No integrity findings",
        (1, true) => "1 finding on this record",
        ({ } count, true) => $"{count} findings on this record",
        (1, false) => "1 finding in the ledger",
        ({ } count, false) => $"{count} findings in the ledger",
    };

    static string Cut(string explanation) =>
        explanation.Length <= MaxExplanationLength
            ? explanation
            : string.Concat(explanation.AsSpan(0, MaxExplanationLength - 1), "…");
}
