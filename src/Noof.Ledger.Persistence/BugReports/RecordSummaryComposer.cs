using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.BugReports;

// The record as a bug report files it and the explainer reads it (spec P-10). Its own queries rather than the trace
// page's summary, which carries no failure reason and keeps free text as typed: the raw text, each line's description,
// each revision's instruction and every wallet and category name lose their fiscal links here. No Telegram chat,
// message or file id, ever.
internal sealed class RecordSummaryComposer(LedgerDbContext db, IFiscalVerificationUrl verificationUrl)
{
    public async Task<string?> ComposeAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var record = await db.Transactions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == transactionId, cancellationToken);
        if (record is null)
            return null;

        var transfer = await db.Transfers.AsNoTracking()
            .SingleOrDefaultAsync(t => t.TransactionId == transactionId, cancellationToken);
        var statement = await db.BalanceChecks.AsNoTracking()
            .SingleOrDefaultAsync(b => b.TransactionId == transactionId, cancellationToken);
        var lines = await (
                from line in db.LineItems.AsNoTracking()
                where line.TransactionId == transactionId
                join category in db.Categories.AsNoTracking() on line.CategoryId equals (Guid?)category.Id into categories
                from category in categories.DefaultIfEmpty()
                orderby line.Ordinal
                select new SummaryLine(line.Description, line.Amount, line.Role, category == null ? null : category.NameEn))
            .ToListAsync(cancellationToken);
        var charges = await db.Charges.AsNoTracking()
            .Where(c => c.TransactionId == transactionId)
            .ToListAsync(cancellationToken);
        var revisions = await db.TransactionRevisions.AsNoTracking()
            .Where(r => r.TransactionId == transactionId)
            .OrderBy(r => r.RevisionNumber)
            .ToListAsync(cancellationToken);

        Guid?[] walletIdsNamed = [record.WalletId, transfer?.FromWalletId, transfer?.ToWalletId];
        List<Guid> walletIds = [.. walletIdsNamed.OfType<Guid>().Distinct()];
        var wallets = await db.Wallets.AsNoTracking()
            .Where(w => walletIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, cancellationToken);
        var wallet = record.WalletId is { } walletId ? wallets.GetValueOrDefault(walletId) : null;

        List<string> summary =
        [
            Header(record),
            $"Date: {record.OccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
        ];

        if (transfer is null)
        {
            summary.Add($"Wallet: {(wallet is null ? "(none)" : Name(wallet.Name))}");
        }
        else
        {
            summary.Add($"From: {WalletName(transfer.FromWalletId)} · {Amount(transfer.From)}");
            summary.Add($"To: {WalletName(transfer.ToWalletId)} · {Amount(transfer.To)}");
            if (transfer.FeeLeg is { } feeLeg && lines.FirstOrDefault(line => line.Role == EntryRole.Fee) is { } fee)
                summary.Add($"Fee: {Amount(fee.Amount)} on the {feeLeg} leg");
            if (transfer is { StatedRate: { } rate, StatedRateBase: { } rateBase })
            {
                var quote = rateBase == transfer.From.Currency ? transfer.To.Currency : transfer.From.Currency;
                summary.Add($"Stated rate: {new ExchangeRate(rateBase, rate, quote)}");
            }
        }

        if (statement is not null)
            summary.Add($"Statement: {Amount(statement.Stated)}");

        summary.Add($"Text: {verificationUrl.StripUrl(record.RawText) ?? "(none)"}");
        summary.AddRange(Section("Lines", lines.Select(Line)));

        if (charges.Count > 0)
        {
            var walletCurrency = wallet?.Currency.Value ?? "(no wallet)";
            summary.Add("Charges:");
            summary.AddRange(charges.OrderBy(charge => charge.Currency).Select(charge => Charge(charge, lines, walletCurrency)));
        }

        summary.AddRange(Section("Revisions", revisions.Select(Revision)));
        return string.Join('\n', summary);

        string WalletName(Guid id) => wallets.TryGetValue(id, out var found) ? Name(found.Name) : id.ToString();
    }

    // Wallet and category names are operator-named free text as well; each is stripped on its own, never the joined
    // summary, whose line breaks a strip touching them would merge.
    string Name(string name) => verificationUrl.StripUrl(name) ?? "";

    static string Header(Transaction record) =>
        $"Record: {record.Kind} · {record.Status}"
        + (record.FailureReason == RecordFailureReason.None ? "" : $" · failure reason {record.FailureReason}")
        + $" · captured from {record.CaptureKind}";

    string Line(SummaryLine line) =>
        $"- {verificationUrl.StripUrl(line.Description) ?? ""} · {Amount(line.Amount)} · {(line.Category is { } category ? Name(category) : "uncategorised")}"
        + (line.Role == EntryRole.Fee ? " · fee" : "");

    static string Charge(Charge charge, IReadOnlyList<SummaryLine> lines, string walletCurrency)
    {
        var priced = lines
            .Where(line => line.Role == EntryRole.Principal && line.Amount.Currency == charge.Currency)
            .Sum(line => line.Amount.Amount);
        return $"- {Number(priced)} {charge.Currency.Value} charged {Number(charge.ChargedAmount)} {walletCurrency} "
            + $"at {charge.RateUsed.ToString("0.############", CultureInfo.InvariantCulture)} "
            + $"· fee {Number(charge.FeeAmount)} {walletCurrency} · {charge.Source}";
    }

    string Revision(TransactionRevision revision) =>
        $"- {revision.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC · "
        + $"{revision.Kind} · {verificationUrl.StripUrl(revision.Instruction) ?? $"{revision.StatusBefore} → {revision.StatusAfter}"}";

    static IEnumerable<string> Section(string name, IEnumerable<string> items)
    {
        List<string> listed = [.. items];
        return listed.Count == 0 ? [$"{name}: (none)"] : [$"{name}:", .. listed];
    }

    static string Amount(Money money) => $"{Number(money.Amount)} {money.Currency.Value}";

    static string Number(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    sealed record SummaryLine(string Description, Money Amount, EntryRole Role, string? Category);
}
