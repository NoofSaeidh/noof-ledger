using System.Globalization;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Chat;

// Everything the bot says about a record. Figures are read from the stored rows, never from a model's
// answer, so the echo shows exactly what the database holds (D4). The bot writes only English for now
// (D-D) - the operator may still write to it in any language.
internal sealed class RecordEcho : IRecordEcho
{
    public string Acknowledgement => "Recording…";
    public string Correcting => "Correcting…";
    public string EditPrompt => "What should I fix? Reply to this message — for example: \"no, 1500\" or \"that was yesterday\".";

    public EchoMessage Failure { get; } = new(
        "Could not read that message. It's saved — reply to this message and tell me how to record it.",
        [RecordAction.Edit]);

    public EchoMessage Compose(CategorizationSubject record) => record switch
    {
        { Status: TransactionStatus.Cancelled } =>
            new($"Cancelled — {record.WalletName}\n{Body(record)}".TrimEnd(), [RecordAction.Restore]),
        { Status: TransactionStatus.Failed } => Failure,
        { Status: TransactionStatus.Captured } => new(Acknowledgement, []),
        { Lines.Count: 0 } =>
            new($"{record.WalletName}: found no spending here — nothing recorded.", [RecordAction.Edit]),
        _ => new($"Recorded — {record.WalletName}\n{Body(record)}", [RecordAction.Cancel, RecordAction.Edit]),
    };

    public EchoMessage ComposeCorrectionFailure(CategorizationSubject record)
    {
        var current = Compose(record);
        return current with { Text = $"Could not apply that correction — the record is unchanged.\n\n{current.Text}" };
    }

    static string Body(CategorizationSubject record)
    {
        List<string> lines = [];

        if (record.OccurredOn != record.SentOn)
            lines.Add($"Date: {record.OccurredOn.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}");

        lines.AddRange(record.Lines.Select(FormatLine));

        if (record.Lines.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Total: {Totals(record.Lines)}");
        }

        return string.Join('\n', lines);
    }

    static string FormatLine(RecordedLine line)
    {
        var text = $"• {line.Description} — {FormatAmount(line.Amount.Amount)} {line.Amount.Currency} · {line.CategoryName ?? "uncategorised"}";
        return line.MerchantName is { } merchant ? $"{text} · {merchant}" : text;
    }

    static string Totals(IReadOnlyList<RecordedLine> lines) => string.Join(", ", lines
        .GroupBy(line => line.Amount.Currency)
        .OrderBy(group => group.Key.Value, StringComparer.Ordinal)
        .Select(group => $"{FormatAmount(group.Sum(line => line.Amount.Amount))} {group.Key}"));

    static string FormatAmount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
