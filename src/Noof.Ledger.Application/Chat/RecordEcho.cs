using System.Globalization;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;
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

    public string Transcribing => "🎤 Transcribing…";

    // Edit, as on Failure: a reply - typed or spoken - still records the purchase through an ordinary correction.
    public EchoMessage HeardNothing { get; } = new("Heard nothing in that voice note.", [RecordAction.Edit]);

    public EchoMessage TranscriptionFailure { get; } = new("Couldn't transcribe that voice note.", [RecordAction.Edit]);

    public EchoMessage Failure { get; } = new(
        "Could not read that message. It's saved — reply to this message and tell me how to record it.",
        [RecordAction.Edit]);

    public string ReadingReceipt => "Reading the receipt…";
    public string OnlyPhotosSupported => "Only photos of receipts are supported.";

    public EchoMessage NotAFiscalReceiptLink { get; } = new("This does not look like a fiscal receipt link.", []);

    public EchoMessage ReceiptFetchUnreachableLinkOnly { get; } = new(
        "The Tax Administration site is unreachable right now — send a photo of the receipt instead.", []);

    public EchoMessage ReceiptReadFailure { get; } = new("Couldn't read that receipt.", []);

    public EchoMessage Compose(CategorizationSubject record) => WithWhatWasHeard(record, record switch
    {
        { Status: TransactionStatus.Cancelled } =>
            new($"Cancelled — {record.WalletName} · balance {Balances(record)}\n{CancelledBody(record)}".TrimEnd(),
                [RecordAction.Restore]),
        { Status: TransactionStatus.Failed } => Failure,
        { Status: TransactionStatus.Captured } => new(Waiting(record), []),
        { Kind: TransactionKind.BalanceCheck, Status: TransactionStatus.Completed } =>
            new(StatementLine(record), [RecordAction.Cancel, RecordAction.Edit]),
        { Kind: TransactionKind.Income, Lines.Count: 0 } =>
            new($"{record.WalletName}: found no income here — nothing recorded.", [RecordAction.Edit]),
        { Lines.Count: 0 } =>
            new($"{record.WalletName}: found no spending here — nothing recorded.", [RecordAction.Edit]),
        { Kind: TransactionKind.Income } =>
            new($"Income — {record.WalletName} · balance {Balances(record)}\n{Body(record)}", [RecordAction.Cancel, RecordAction.Edit]),
        _ => new($"Recorded — {record.WalletName} · balance {Balances(record)}\n{Body(record)}", [RecordAction.Cancel, RecordAction.Edit]),
    });

    public EchoMessage ComposeHeardNothing(CategorizationSubject record)
    {
        var current = Compose(record);
        return current with { Text = $"{HeardNothing.Text}\n\n{current.Text}" };
    }

    public EchoMessage ComposeCorrectionFailure(CategorizationSubject record)
    {
        var current = Compose(record);
        return current with { Text = $"Could not apply that correction — the record is unchanged.\n\n{current.Text}" };
    }

    public string ComposeCategorisingReceipt(int lineCount) =>
        $"Categorising {lineCount} line{(lineCount == 1 ? "" : "s")}…";

    public EchoMessage ComposeReceiptDuplicate(DateOnly? occurredOn, decimal total, CurrencyCode currency)
    {
        var reference = occurredOn is { } date
            ? $"{date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}, {FormatAmount(total)} {currency}"
            : $"{FormatAmount(total)} {currency}";

        return new($"Already recorded — this receipt was sent before ({reference}).", []);
    }

    // Telegram's own hard limit (R-2, R-7). Kept well clear of it rather than measured live: 40
    // detailed lines plus a grouped tail is short enough for any real receipt to stay under 4096
    // characters without a second, length-dependent pass over the same text.
    const int MaxDetailedReceiptLines = 40;

    public EchoMessage ComposeReceipt(CategorizationSubject record, ReceiptView receipt) =>
        new(
            $"Recorded — {ShopHeader(receipt)} · {record.WalletName} · balance {Balances(record)}\n{ReceiptBody(record, receipt)}",
            [RecordAction.Cancel, RecordAction.Edit]);

    public EchoMessage ComposeReceiptNotRecorded(Receipts.ReceiptKind kind) =>
        new($"This receipt is a {kind.ToString().ToLowerInvariant()} — not recorded", [RecordAction.Edit]);

    static string ShopHeader(ReceiptView receipt)
    {
        var name = receipt.SellerName is { Length: > 0 } sellerName ? sellerName : "Receipt";
        return receipt.LocationName is { Length: > 0 } location ? $"{name} — {location}" : name;
    }

    static string ReceiptBody(CategorizationSubject record, ReceiptView receipt)
    {
        List<string> lines = [$"Date: {record.OccurredOn.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}"];
        lines.AddRange(ReceiptLineSection(record.Lines));
        lines.Add(string.Empty);
        lines.Add($"Total: {Totals(record.Lines)}");
        lines.AddRange(ReceiptWarnings(record.Lines, receipt));
        return string.Join('\n', lines);
    }

    static IEnumerable<string> ReceiptLineSection(IReadOnlyList<RecordedLine> lines)
    {
        if (lines.Count <= MaxDetailedReceiptLines)
            return lines.Select(FormatLine);

        var detailed = lines.Take(MaxDetailedReceiptLines).Select(FormatLine);
        var remainder = lines.Skip(MaxDetailedReceiptLines).ToList();
        var grouped = remainder
            .GroupBy(line => line.CategoryName ?? "uncategorised")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}: {FormatAmount(group.Sum(line => line.Amount.Amount))} {group.First().Amount.Currency}");

        return [.. detailed, $"… {remainder.Count} more lines", .. grouped];
    }

    static IEnumerable<string> ReceiptWarnings(IReadOnlyList<RecordedLine> lines, ReceiptView receipt)
    {
        if (receipt is { Source: Receipts.ReceiptSource.Vision, QrTotal: not null })
            yield return "⚠️ Tax Administration unavailable — lines read from the photo";
        else if (receipt is { Source: Receipts.ReceiptSource.Vision, QrTotal: null })
            yield return "⚠️ Read from the photo (no fiscal QR)";

        var sum = lines.Sum(line => line.Amount.Amount);
        if (Math.Abs(sum - receipt.Total) > 0.01m)
        {
            yield return
                $"⚠️ Lines add up to {FormatAmount(sum)} {receipt.Currency}, the receipt says {FormatAmount(receipt.Total)} {receipt.Currency}";
        }
    }

    string Waiting(CategorizationSubject record) =>
        record is { CaptureKind: CaptureKind.Voice, RawText.Length: 0 } ? Transcribing : Acknowledgement;

    // A voice record's echo opens with what was heard (V5), so a misheard word is told apart from a misread one.
    static EchoMessage WithWhatWasHeard(CategorizationSubject record, EchoMessage echo) =>
        record is { CaptureKind: CaptureKind.Voice, RawText.Length: > 0, Status: not TransactionStatus.Failed }
            ? echo with { Text = $"🎤 \"{record.RawText}\"\n{echo.Text}" }
            : echo;

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

        // M10: a spending in a currency other than its wallet's is not converted - visible as a
        // separate currency line in the balance, and flagged here so it never looks like an
        // oversight.
        if (record.WalletCurrency is { } currency && record.Lines.Any(line => line.Amount.Currency != currency))
            lines.Add("Not in the wallet's currency — no conversion yet.");

        return string.Join('\n', lines);
    }

    // A BalanceCheck's own body is the statement it recorded, not a line-item body - it has no lines
    // (the mapper discards a balance statement's Items, per the contract).
    static string CancelledBody(CategorizationSubject record) =>
        record is { Kind: TransactionKind.BalanceCheck, Statement: { } statement }
            ? $"Statement: {FormatAmount(statement.Stated.Amount)} {statement.Stated.Currency}"
            : Body(record);

    static string StatementLine(CategorizationSubject record)
    {
        // A Completed BalanceCheck always has a balance_checks row - the mapper and RewriteAsync both
        // guarantee it - but a null-forgiving `!` would turn a bug into a crashed Telegram edit rather
        // than a wrong-looking message, so a missing row degrades instead of throwing.
        if (record.Statement is not { } statement)
            return $"{record.WalletName}: balance statement recorded.";

        var currency = statement.Stated.Currency;
        var before = statement.ComputedBefore;
        var stated = statement.Stated.Amount;
        var diff = stated - before;
        var tail = diff == 0m
            ? "matches"
            : $"adjusted {(diff > 0 ? "+" : "-")}{FormatAmount(Math.Abs(diff))} {currency}";

        return $"{record.WalletName}: balance was {FormatAmount(before)} {currency}, you said {FormatAmount(stated)} {currency} — {tail}";
    }

    static string Balances(CategorizationSubject record)
    {
        if (record.WalletBalances is not { Count: > 0 } balances)
            return record.WalletCurrency is { } currency ? $"0.00 {currency}" : "0.00";

        return string.Join(", ", balances.Select(money => $"{FormatAmount(money.Amount)} {money.Currency}"));
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
