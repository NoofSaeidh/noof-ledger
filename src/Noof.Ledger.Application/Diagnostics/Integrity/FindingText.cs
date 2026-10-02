using System.Globalization;

namespace Noof.Ledger.Application.Diagnostics.Integrity;

internal sealed class FindingText
{
    public string Title(IntegrityCheck check) => check switch
    {
        IntegrityCheck.PostingsDisagree => "Postings disagree with their sources",
        IntegrityCheck.FactsMismatchKind => "A record's facts do not match its kind",
        IntegrityCheck.StuckInPipeline => "Stuck in the pipeline",
        IntegrityCheck.NotApplied => "Not applied",
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, null),
    };

    public string Description(IntegrityCheck check) => check switch
    {
        IntegrityCheck.PostingsDisagree =>
            "A record's ledger entries differ from what its lines, charges and transfer legs add up to, sit on a wallet "
            + "the record does not use, or a foreign charge is priced differently from the lines it prices. The wallet's "
            + "balance is then wrong. It usually means the app wrote the entries incorrectly — a bug in the app.",
        IntegrityCheck.FactsMismatchKind =>
            "A record's stored facts contradict its kind: a transfer without its legs or with a spending line, a "
            + "statement without its checkpoint, a foreign charge on something that is not a spending or in the "
            + "wallet's own currency, a fee leg that does not match its fee line, a leg or checkpoint on the wrong "
            + "wallet or currency, or a failure reason on a record that is not failed. It usually means the app wrote "
            + "the record inconsistently — a bug in the app.",
        IntegrityCheck.StuckInPipeline =>
            "A message was captured more than 10 minutes ago and nothing is working on it: no job is queued, running "
            + "or failed, and it is not waiting for Record anyway. It usually means the app never queued the work for "
            + "it — a bug in the app.",
        IntegrityCheck.NotApplied =>
            "Something has waited on the operator for more than a day: a record whose first reading failed (the bot "
            + "asked for something, or could not read it), a receipt or exchange slip waiting for Record anyway, a "
            + "correction, re-read or transcription that never applied, or a record restored after a cancel that "
            + "nothing will process. The operator answers by replying to the echo with what is missing, pressing "
            + "Record anyway, or cancelling the record.",
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, null),
    };

    public string GroupLabel(IntegrityGroup group) => group switch
    {
        IntegrityGroup.Bug => "Bug",
        IntegrityGroup.WaitingOnYou => "Waiting on you",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null),
    };

    public string FormatValue(IntegrityFact fact, DateTimeOffset asOf) => fact switch
    {
        MoneyFact money => string.Create(CultureInfo.InvariantCulture, $"{money.Amount:0.00} {money.Currency}"),
        DateFact date => date.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        SinceFact since => SinceValue(since.Since, asOf),
        TextFact text => text.Text,
        CountFact count => count.Count.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(fact), fact.GetType().Name, null),
    };

    public string Format(IntegrityFact fact, DateTimeOffset asOf) => $"{fact.Name}: {FormatValue(fact, asOf)}";

    public string FindingsBlock(IReadOnlyList<IntegrityFinding> findings, DateTimeOffset asOf) =>
        string.Join("\n", findings.SelectMany((finding, index) => FindingLines(finding, index + 1, asOf)));

    IEnumerable<string> FindingLines(IntegrityFinding finding, int number, DateTimeOffset asOf) =>
    [
        string.Create(CultureInfo.InvariantCulture, $"{number}. {Title(finding.Check)} ({GroupLabel(finding.Group)})"),
        .. finding.Facts.Select(fact => $"   {Format(fact, asOf)}"),
    ];

    // A start after asOf - demo data, a clock change - is a wait that has not begun, not a negative age.
    static string SinceValue(DateTimeOffset since, DateTimeOffset asOf)
    {
        var age = asOf > since ? asOf - since : TimeSpan.Zero;
        return string.Create(CultureInfo.InvariantCulture, $"{Age(age)} (since {since.UtcDateTime:yyyy-MM-dd HH:mm} UTC)");
    }

    static string Age(TimeSpan age) => age switch
    {
        { TotalDays: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{age.Days} d {age.Hours} h"),
        { TotalHours: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{age.Hours} h {age.Minutes} min"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{age.Minutes} min"),
    };
}
