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

    public EchoMessage TranscriptionFailure { get; } = new(
        "Couldn't transcribe that voice note. Reply to this message and type what to record.",
        [RecordAction.Edit]);

    public EchoMessage Failure { get; } = new(
        "Could not read that message. It's saved — reply to this message and tell me how to record it.",
        [RecordAction.Edit]);

    public string ReadingReceipt => "Reading the receipt…";
    public string OnlyPhotosSupported => "Only photos of receipts are supported.";

    public string UnsupportedMessageType =>
        "I can only read text, voice notes, receipt photos, or a receipt's QR link.";

    public EchoMessage NotAFiscalReceiptLink { get; } = new("This does not look like a fiscal receipt link.", []);

    public EchoMessage ReceiptFetchUnreachableLinkOnly { get; } = new(
        "The Tax Administration site is unreachable right now — send a photo of the receipt instead.", []);

    public EchoMessage ReceiptReadFailure { get; } = new(
        "Couldn't read that receipt. " + "Send the link from the receipt's QR code (scan it with your phone camera).",
        []);

    public EchoMessage ReceiptVisionNotConfigured { get; } = new(
        "This receipt has no readable fiscal QR code, and no AI key is set up to read it from the photo — " +
        "add one in Settings, or resend a receipt with a fiscal QR visible.", []);

    public EchoMessage NewReceiptLinkMustBeSentSeparately { get; } = new(
        "This looks like a different receipt — send it as its own new message. Nothing changed here.", []);

    public EchoMessage Compose(CategorizationSubject record) => WithWhatWasHeard(record, record switch
    {
        { Kind: TransactionKind.Transfer, Transfer: { } transfer, Status: TransactionStatus.Cancelled } =>
            new(TransferText(record, transfer, cancelled: true), [RecordAction.Restore]),
        { Status: TransactionStatus.Cancelled } =>
            new($"Cancelled — {record.WalletName} · balance {Balances(record)}\n{CancelledBody(record)}".TrimEnd(),
                [RecordAction.Restore]),
        { Status: TransactionStatus.Failed } => Failure,
        { Status: TransactionStatus.Captured } => new(Waiting(record), []),
        { Kind: TransactionKind.Transfer, Transfer: { } transfer } =>
            new(TransferText(record, transfer, cancelled: false), [RecordAction.Cancel, RecordAction.Edit]),
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

    public EchoMessage ComposeCorrectionFailure(CategorizationSubject record, RecordFailureReason reason = RecordFailureReason.None)
    {
        var current = Compose(record);
        return current with { Text = $"Could not apply that correction — the record is unchanged.\n\n{current.Text}" };
    }

    const string ReceiptExtractionStep = "Reading the receipt";
    const string ReceiptCategorizationStep = "Categorising the receipt";

    public EchoMessage ComposeCategorizationRetryNotice(string step, Exception failure, DateTimeOffset nextAttemptLocal) =>
        ComposeRetryNotice(step, SafeFailureReason.Describe(failure, FailureArea.Categorization), nextAttemptLocal);

    public EchoMessage ComposeReceiptCategorizationRetryNotice(Exception failure, DateTimeOffset nextAttemptLocal) =>
        ComposeRetryNotice(
            ReceiptCategorizationStep, SafeFailureReason.Describe(failure, FailureArea.ReceiptCategorization), nextAttemptLocal);

    public EchoMessage ComposeReceiptExtractionRetryNotice(Exception failure, DateTimeOffset nextAttemptLocal) =>
        ComposeRetryNotice(ReceiptExtractionStep, SafeFailureReason.Describe(failure, FailureArea.ReceiptExtraction), nextAttemptLocal);

    public EchoMessage ComposeTranscriptionRetryNotice(string step, Exception failure, DateTimeOffset nextAttemptLocal) =>
        ComposeRetryNotice(step, SafeFailureReason.Describe(failure, FailureArea.Transcription), nextAttemptLocal);

    static EchoMessage ComposeRetryNotice(string step, string reason, DateTimeOffset nextAttemptLocal) =>
        new($"⚠️ {step} hit a problem ({reason}) — retrying around {nextAttemptLocal.ToString("HH:mm", CultureInfo.InvariantCulture)}.",
            []);

    public string ComposeCategorisingReceipt(int lineCount) =>
        $"Categorising {lineCount} line{(lineCount == 1 ? "" : "s")}…";

    public EchoMessage ComposeReceiptDuplicate(DateOnly? occurredOn, decimal total, CurrencyCode currency, bool originalCancelled = false)
    {
        var reference = occurredOn is { } date
            ? $"{date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}, {FormatAmount(total)} {currency}"
            : $"{FormatAmount(total)} {currency}";

        return originalCancelled
            ? new($"Already recorded — this receipt was sent before ({reference}) and cancelled. "
                + "Press Restore on that message to bring it back.", [])
            : new($"Already recorded — this receipt was sent before ({reference}).", []);
    }

    // Telegram's own hard limit (R-2, R-7). Kept well clear of it rather than measured live: 40
    // detailed lines plus a grouped tail is short enough for any real receipt to stay under 4096
    // characters without a second, length-dependent pass over the same text.
    const int MaxDetailedReceiptLines = 40;

    const string AmountChangeDeclinedNote = "Amounts come from the receipt and cannot be changed here — press Cancel if this record is wrong.";
    const string DateChangeDeclinedNote = "The date comes from the receipt and cannot be changed here — press Cancel if this record is wrong.";

    // M-3 (Phase 6 final review): Cancel/Restore call this too (RecordActionHandler), so a receipt
    // transaction keeps its shop header and its lines' own order through both states, instead of
    // falling back to the generic Compose the moment it is Cancelled.
    public EchoMessage ComposeReceipt(
        CategorizationSubject record, ReceiptView receipt, UnsupportedChangeKind unsupportedChange = UnsupportedChangeKind.None)
    {
        // N-5 (Phase 6 re-review): M-4 (a non-money slip's own Cancelled) and M-11 (a duplicate's
        // Restore) both leave RecordActionHandler re-rendering a receipt record that never reached
        // Persisted - Captured (Restore before ApplyAsync ever ran) or Failed. Neither is
        // "Recorded … Total: " for a record with no line items; both defer to the same rendering the
        // ordinary, non-receipt echo already gives that status.
        // R2-4 (Phase 6 second re-review): a non-money slip (Copy/Training/Proforma/Advance) Captured
        // with no job pending - the same M-4/M-11 paths above - rendered "Recording…" forever with no
        // [Edit], a dead end promising work nobody will do. It gets the honest not-recorded rendering
        // instead; an ordinary receipt still waiting on extraction gets the receipt-specific wording.
        if (record.Status == TransactionStatus.Captured)
            return receipt.Kind.IsNonMoneyKind() ? ComposeReceiptNotRecorded(receipt.Kind) : new(ReadingReceipt, []);

        if (record.Status == TransactionStatus.Failed)
            return Failure;

        var header = record.Status == TransactionStatus.Cancelled ? "Cancelled" : "Recorded";
        var text = $"{header} — {ShopHeader(receipt)} · {record.WalletName} · balance {Balances(record)}\n{ReceiptBody(record, receipt)}";

        if (record.Status == TransactionStatus.Cancelled)
            return new(text, [RecordAction.Restore]);

        var note = unsupportedChange switch
        {
            UnsupportedChangeKind.Date => DateChangeDeclinedNote,
            UnsupportedChangeKind.Amount => AmountChangeDeclinedNote,
            _ => null,
        };

        return new(note is null ? text : $"{note}\n\n{text}", [RecordAction.Cancel, RecordAction.Edit]);
    }

    public EchoMessage ComposeReceiptNotRecorded(ReceiptKind kind) =>
        new($"This receipt is a {kind.ToString().ToLowerInvariant()} — not recorded", [RecordAction.Edit]);

    public EchoMessage ReceiptUnreadable { get; } = new(
        "I couldn't read this receipt reliably, so nothing was recorded. " + "Send the link from the receipt's QR code (scan it with your phone camera).", []);

    public EchoMessage ComposeReceiptNeedsConfirmation(ExtractedReceipt receipt, bool taxIdMalformed = false, bool kindUnclear = false) =>
        ComposeReceiptNeedsConfirmationCore(
            receipt.SellerName, receipt.LocationName, receipt.IssuedAt, receipt.SellerTaxId, receipt.FiscalNumber,
            receipt.Lines.Select(line => (line.Name, line.Total)), receipt.Currency, receipt.Total,
            receipt.QrTotal ?? receipt.Total, taxIdMalformed, kindUnclear);

    public EchoMessage ComposeReceiptNeedsConfirmation(ReceiptView receipt, bool taxIdMalformed = false, bool kindUnclear = false) =>
        ComposeReceiptNeedsConfirmationCore(
            receipt.SellerName, receipt.LocationName, receipt.IssuedAt, receipt.SellerTaxId, receipt.FiscalNumber,
            receipt.Lines.Select(line => (line.Name, line.Total)), receipt.Currency, receipt.Total,
            receipt.QrTotal ?? receipt.Total, taxIdMalformed, kindUnclear);

    // Owns both the mismatch arithmetic and the wording (the same split ReceiptWarnings already makes
    // for the recorded echo), so an ExtractedReceipt fresh off the vision fallback and a ReceiptView
    // read back later (RecordActionHandler's Cancel/Restore, ExtractReceiptWorker's own C-1 replay)
    // produce byte-identical prompts instead of two hand-maintained copies of the same sentence.
    static EchoMessage ComposeReceiptNeedsConfirmationCore(
        string? sellerName, string? locationName, DateTimeOffset? issuedAt, string? sellerTaxId, string? fiscalNumber,
        IEnumerable<(string Name, decimal Total)> receiptLines, CurrencyCode currency, decimal total, decimal referenceTotal,
        bool taxIdMalformed, bool kindUnclear = false)
    {
        var name = sellerName is { Length: > 0 } ? sellerName : "Receipt";
        var header = locationName is { Length: > 0 } location ? $"{name} — {location}" : name;
        var lineList = receiptLines.ToList();

        List<string> lines = [$"This receipt doesn't look right — {header}"];
        if (issuedAt is { } at)
            lines.Add($"Date: {at.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}");
        if (sellerTaxId is { Length: > 0 })
            lines.Add($"PIB: {sellerTaxId}");
        if (fiscalNumber is { Length: > 0 })
            lines.Add($"Fiscal #: {fiscalNumber}");
        lines.AddRange(CapDetailedLines(lineList, line => $"• {line.Name} — {FormatAmount(line.Total)} {currency}", MaxDetailedReceiptLines));
        lines.Add(string.Empty);
        lines.Add($"Total: {FormatAmount(total)} {currency}");
        lines.Add(string.Empty);

        var sum = lineList.Sum(line => line.Total);
        if (HasMismatch(sum, referenceTotal))
            lines.Add($"⚠️ Lines add up to {FormatAmount(sum)} {currency}, the receipt says {FormatAmount(referenceTotal)} {currency}");
        if (taxIdMalformed)
            lines.Add("⚠️ The printed tax id does not look like a valid PIB (9 digits)");
        if (kindUnclear)
            lines.Add("⚠️ The receipt type could not be read");

        // 2026-09-27: this prompt only ever shows for a vision receipt (never a fiscal QR/SUF one), so
        // it carries the same standing hint every other vision echo does (ReceiptWarnings).
        lines.Add("⚠️ For an exact read next time, send the link from the receipt's QR code (scan it with your phone camera) instead of a photo.");
        lines.Add(string.Empty);
        lines.Add("Record it anyway, or cancel?");

        return new(string.Join('\n', lines), [RecordAction.RecordAnyway, RecordAction.Cancel]);
    }

    static bool HasMismatch(decimal sum, decimal referenceTotal) => Math.Abs(sum - referenceTotal) > 0.01m;

    public EchoMessage ComposeReceiptCancelledUnconfirmed(CategorizationSubject record, ReceiptView receipt)
    {
        var header = $"Cancelled — {ShopHeader(receipt)} · {record.WalletName} · balance {Balances(record)}";
        var total = $"Total: {FormatAmount(receipt.Total)} {receipt.Currency}";
        return new($"{header}\n{total}\n\nThis receipt was never categorised — press Restore to bring it back for confirmation.",
            [RecordAction.Restore]);
    }

    static string ShopHeader(ReceiptView receipt)
    {
        var name = receipt.SellerName is { Length: > 0 } sellerName ? sellerName : "Receipt";
        return receipt.LocationName is { Length: > 0 } location ? $"{name} — {location}" : name;
    }

    static string ReceiptBody(CategorizationSubject record, ReceiptView receipt)
    {
        List<string> lines = [DateLine(record.OccurredOn)];
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

        var remainder = lines.Skip(MaxDetailedReceiptLines).ToList();
        var grouped = remainder
            .GroupBy(line => line.CategoryName ?? "uncategorised")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}: {FormatAmount(group.Sum(line => line.Amount.Amount))} {group.First().Amount.Currency}");

        return [.. CapDetailedLines(lines, FormatLine, MaxDetailedReceiptLines), .. grouped];
    }

    // Shared between the recorded echo (which adds its own per-category grouping after the "… N more
    // lines" summary) and the vision confirmation prompt (which has no categories yet - the operator
    // still has to press Record anyway before CategorizeReceipt ever runs) - one cap, one wording for
    // the omitted-lines line, since Telegram's 4096-character limit applies to both alike.
    static IEnumerable<string> CapDetailedLines<T>(IReadOnlyList<T> items, Func<T, string> format, int cap)
    {
        if (items.Count <= cap)
            return items.Select(format);

        var omitted = items.Count - cap;
        return [.. items.Take(cap).Select(format), $"… {omitted} more lines"];
    }

    static IEnumerable<string> ReceiptWarnings(IReadOnlyList<RecordedLine> lines, ReceiptView receipt)
    {
        if (receipt is { Source: ReceiptSource.Vision, QrTotal: not null })
            yield return "⚠️ Tax Administration unavailable — lines read from the photo";
        else if (receipt is { Source: ReceiptSource.Vision, QrTotal: null })
            yield return "⚠️ Read from the photo (no fiscal QR)";

        var sum = lines.Sum(line => line.Amount.Amount);
        if (Math.Abs(sum - receipt.Total) > 0.01m)
        {
            yield return
                $"⚠️ Lines add up to {FormatAmount(sum)} {receipt.Currency}, the receipt says {FormatAmount(receipt.Total)} {receipt.Currency}";
        }

        // 2026-09-27: every vision read is a fallback the QR path could not take - worth naming a way
        // to get an exact read next time, independent of whether this particular read happened to add
        // up. Not "send it as a file": the operator's own real receipts (docs/decisions/p6-1-receipts-decisions.md,
        // Phase 6 QR entry) showed a photo, even a full-resolution one sent as a file, does not reliably decode
        // the fiscal QR either - only the link, scanned by the phone's own camera, does.
        if (receipt.Source == ReceiptSource.Vision)
        {
            yield return "⚠️ For an exact read next time, send the link from the receipt's QR code (scan it with your phone camera) instead of a photo.";
        }
    }

    // R2-3 follow-up: a Captured photo is a receipt still being read, not a transcript being
    // recorded - it gets the same "Reading the receipt…" wording ComposeReceipt and the initial
    // capture acknowledgement (TelegramUpdateRouter) already give this state.
    string Waiting(CategorizationSubject record) => record switch
    {
        { CaptureKind: CaptureKind.Voice, RawText.Length: 0 } => Transcribing,
        { CaptureKind: CaptureKind.Photo } => ReadingReceipt,
        _ => Acknowledgement,
    };

    // A voice record's echo opens with what was heard (V5), so a misheard word is told apart from a misread one.
    static EchoMessage WithWhatWasHeard(CategorizationSubject record, EchoMessage echo) =>
        record is { CaptureKind: CaptureKind.Voice, RawText.Length: > 0, Status: not TransactionStatus.Failed }
            ? echo with { Text = $"🎤 \"{record.RawText}\"\n{echo.Text}" }
            : echo;

    static string Body(CategorizationSubject record)
    {
        List<string> lines = [];

        if (record.OccurredOn != record.SentOn)
            lines.Add(DateLine(record.OccurredOn));

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

    static string Balances(CategorizationSubject record) => Balances(record.WalletBalances, record.WalletCurrency);

    static string Balances(IReadOnlyList<Money>? balances, CurrencyCode? currency)
    {
        if (balances is not { Count: > 0 })
            return currency is { } code ? $"0.00 {code}" : "0.00";

        return string.Join(", ", balances.Select(FormatMoney));
    }

    // A transfer is read from its transfers row, never from its lines: it has no principal lines, and its one
    // fee line is shown as the fee it is, never as a purchase.
    static string TransferText(CategorizationSubject record, TransferView transfer, bool cancelled)
    {
        List<string> lines = [TransferHeader(transfer, cancelled)];

        if (record.OccurredOn != record.SentOn)
            lines.Add(DateLine(record.OccurredOn));

        if (transfer.Fee is { } fee)
            lines.Add($"Fee {FormatMoney(fee)} · {FeeCategory(record)}");

        lines.Add($"{transfer.FromWalletName} · balance {Balances(transfer.FromBalances, transfer.From.Currency)}");
        lines.Add($"{transfer.ToWalletName} · balance {Balances(transfer.ToBalances, transfer.To.Currency)}");

        if (!cancelled && SourceCrossedZero(transfer) is { } now)
            lines.Add($"{transfer.FromWalletName} is now {FormatMoney(now)} — a missing exchange or income?");

        return string.Join('\n', lines);
    }

    // Only a crossing this transfer caused: a wallet that was already below zero (a credit card) stays quiet, and
    // so does a backdated transfer a later checkpoint absorbed - the balance without it comes from
    // GetSubjectAsync, by the wallet_balances rule, never "now plus the amount".
    static Money? SourceCrossedZero(TransferView transfer)
    {
        if (transfer.FromBalanceWithoutThis is not >= 0m)
            return null;

        var now = transfer.FromBalances
            .Where(balance => balance.Currency == transfer.From.Currency)
            .Select(balance => (Money?)balance)
            .FirstOrDefault();

        return now is { Amount: < 0m } ? now : null;
    }

    static string TransferHeader(TransferView transfer, bool cancelled)
    {
        var exchange = transfer.From.Currency != transfer.To.Currency;
        var title = (exchange, cancelled) switch
        {
            (true, false) => "Exchange",
            (true, true) => "Cancelled exchange",
            (false, false) => "Transfer",
            (false, true) => "Cancelled transfer",
        };
        var fromFee = FeeNote(transfer, TransferLeg.From);
        var toFee = FeeNote(transfer, TransferLeg.To);

        if (exchange)
        {
            return $"{title} — {FormatMoney(transfer.From)} ({WithNote(transfer.FromWalletName, fromFee)}) → "
                + $"{FormatMoney(transfer.To)} ({WithNote(transfer.ToWalletName, toFee)}) · {RateOf(transfer)}";
        }

        if (transfer.Fee is null)
            return $"{title} — {FormatMoney(transfer.From)} · {transfer.FromWalletName} → {transfer.ToWalletName}";

        return $"{title} — {transfer.FromWalletName} -{FormatMoney(transfer.From)}{Parenthesised(fromFee)} → "
            + $"{transfer.ToWalletName} +{FormatMoney(transfer.To)}{Parenthesised(toFee)}";
    }

    // The source leg's stored amount already includes its fee, and the destination's already lost it (T-12):
    // "incl." and "after" say which.
    static string? FeeNote(TransferView transfer, TransferLeg leg) => transfer switch
    {
        { Fee: { } fee, FeeLeg: { } feeLeg } when feeLeg == leg =>
            $"{(leg == TransferLeg.From ? "incl." : "after")} fee {FormatAmount(fee.Amount)}",
        _ => null,
    };

    static string WithNote(string walletName, string? note) => note is null ? walletName : $"{walletName}, {note}";

    static string Parenthesised(string? note) => note is null ? string.Empty : $" ({note})";

    // A stated rate is shown as stated; otherwise it is derived from the principals, never from a leg that
    // carries the fee.
    static ExchangeRate RateOf(TransferView transfer) =>
        transfer.StatedRate ?? ExchangeRate.Between(SourcePrincipal(transfer), DestinationPrincipal(transfer));

    static Money SourcePrincipal(TransferView transfer) =>
        transfer is { Fee: { } fee, FeeLeg: TransferLeg.From } ? transfer.From - fee : transfer.From;

    static Money DestinationPrincipal(TransferView transfer) =>
        transfer is { Fee: { } fee, FeeLeg: TransferLeg.To } ? transfer.To + fee : transfer.To;

    static string FeeCategory(CategorizationSubject record) =>
        record.Lines.FirstOrDefault(line => line.Role == EntryRole.Fee)?.CategoryName ?? "uncategorised";

    static string DateLine(DateOnly day) => $"Date: {day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}";

    static string FormatMoney(Money money) => $"{FormatAmount(money.Amount)} {money.Currency}";

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
