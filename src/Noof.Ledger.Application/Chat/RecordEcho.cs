using System.Globalization;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Chat;

// Everything the bot says about a record. Figures are read from the stored rows, never from a model's
// answer, so the echo shows exactly what the database holds (D4).
public static class RecordEcho
{
    public const string Acknowledgement = "Записываю…";
    public const string Correcting = "Исправляю…";
    public const string EditPrompt = "Что исправить? Ответьте на это сообщение — например: «нет, 1500» или «это было вчера».";

    public static readonly EchoMessage Failure = new(
        "Не смог разобрать это сообщение. Оно сохранено — ответьте на это сообщение и напишите, как его записать.",
        [RecordAction.Edit]);

    public static EchoMessage Compose(CategorizationSubject record) => record switch
    {
        { Status: TransactionStatus.Cancelled } =>
            new($"Отменено — {record.WalletName}\n{Body(record)}".TrimEnd(), [RecordAction.Restore]),
        { Status: TransactionStatus.Failed } => Failure,
        { Status: TransactionStatus.Captured } => new(Acknowledgement, []),
        { Lines.Count: 0 } =>
            new($"{record.WalletName}: не нашёл здесь трат — ничего не записал.", [RecordAction.Edit]),
        _ => new($"Записал — {record.WalletName}\n{Body(record)}", [RecordAction.Cancel, RecordAction.Edit]),
    };

    public static EchoMessage ComposeCorrectionFailure(CategorizationSubject record)
    {
        var current = Compose(record);
        return current with { Text = $"Не получилось применить исправление — запись не изменилась.\n\n{current.Text}" };
    }

    static string Body(CategorizationSubject record)
    {
        List<string> lines = [];

        if (record.OccurredOn != record.SentOn)
            lines.Add($"Дата: {record.OccurredOn.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}");

        lines.AddRange(record.Lines.Select(FormatLine));

        if (record.Lines.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Итого: {Totals(record.Lines)}");
        }

        return string.Join('\n', lines);
    }

    static string FormatLine(RecordedLine line)
    {
        var text = $"• {line.Description} — {FormatAmount(line.Amount.Amount)} {line.Amount.Currency} · {line.CategoryName ?? "без категории"}";
        return line.MerchantName is { } merchant ? $"{text} · {merchant}" : text;
    }

    static string Totals(IReadOnlyList<RecordedLine> lines) => string.Join(", ", lines
        .GroupBy(line => line.Amount.Currency)
        .OrderBy(group => group.Key.Value, StringComparer.Ordinal)
        .Select(group => $"{FormatAmount(group.Sum(line => line.Amount.Amount))} {group.Key}"));

    static string FormatAmount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
