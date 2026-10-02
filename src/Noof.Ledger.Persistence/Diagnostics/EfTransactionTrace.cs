using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Reporting;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Diagnostics;

// AwaitingConfirmation shares IReceiptStore.IsAwaitingConfirmationAsync with RecordActionHandler's
// Cancel/Restore rather than re-deriving job existence inline (2026-09-27 finding: the two had drifted
// - this one additionally required Status == Captured, so a Cancelled-but-still-unconfirmed receipt
// showed as confirmed on the trace page while the bot's own echo still offered Restore for it). That
// store method is deliberately status-independent - a vision receipt with no CategorizeReceipt job is
// "awaiting" whether it is Captured or Cancelled - and the trace page, a read-only diagnostic view,
// has no reason to narrow that any further than the one definition already uses.
internal sealed class EfTransactionTrace(LedgerDbContext db, IReceiptStore receiptStore) : ITransactionTrace
{
    public async Task<TransactionTrace> GetAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var summary = await LoadSummaryAsync(transactionId, cancellationToken);

        var rows = await db.AppLogs.AsNoTracking()
            .Where(e => e.TransactionId == transactionId && e.PropertiesJson != null)
            .OrderBy(e => e.LoggedAt).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

        var events = rows
            .Select(ToTraceEvent)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();

        var historyViews = await HistoryAsync(transactionId, cancellationToken);

        var receipt = await ReceiptTraceAsync(transactionId, cancellationToken);

        return new TransactionTrace(transactionId, summary is not null, summary, events, historyViews, receipt);
    }

    async Task<TransactionSummary?> LoadSummaryAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var header = await (
                from t in db.Transactions.AsNoTracking()
                where t.Id == transactionId
                join w in db.Wallets.AsNoTracking() on t.WalletId equals (Guid?)w.Id into walletJoin
                from w in walletJoin.DefaultIfEmpty()
                select new
                {
                    t.RawText,
                    t.CaptureKind,
                    t.CreatedAt,
                    t.Status,
                    t.Kind,
                    t.OccurredOn,
                    Wallet = w,
                })
            .SingleOrDefaultAsync(cancellationToken);

        if (header is null)
            return null;

        var lineItems = await (
                from li in db.LineItems.AsNoTracking()
                where li.TransactionId == transactionId
                join c in db.Categories.AsNoTracking() on li.CategoryId equals c.Id into categoryJoin
                from c in categoryJoin.DefaultIfEmpty()
                orderby li.Id
                select new TraceLineItem(li.Description, li.Amount, c == null ? null : c.NameEn, li.Role))
            .ToListAsync(cancellationToken);

        var transfer = header.Kind == TransactionKind.Transfer ? await TransferAsync(transactionId, cancellationToken) : null;
        IReadOnlyList<ChargeView> charges = header.Wallet is { } wallet
            ? await ChargesAsync(transactionId, wallet.Currency, lineItems, cancellationToken)
            : [];

        return new TransactionSummary(
            header.RawText,
            header.CaptureKind,
            header.CreatedAt,
            header.Status,
            header.Kind,
            header.Wallet?.Name,
            header.OccurredOn,
            lineItems,
            transfer,
            charges);
    }

    async Task<TransferTraceView?> TransferAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var lines = await TransferLines.ForAsync(db, [transactionId], cancellationToken);
        if (!lines.TryGetValue(transactionId, out var line))
            return null;

        var facts = await (
                from transfer in db.Transfers.AsNoTracking()
                where transfer.TransactionId == transactionId
                join venue in db.Merchants.AsNoTracking() on transfer.VenueMerchantId equals (Guid?)venue.Id into venueJoin
                from venue in venueJoin.DefaultIfEmpty()
                select new { RateStated = transfer.StatedRate != null, VenueName = venue == null ? null : venue.DisplayName })
            .SingleOrDefaultAsync(cancellationToken);

        // A correction can delete the row between the two reads; nothing holds them in one transaction.
        return facts is null ? null : new TransferTraceView(line, facts.RateStated, facts.VenueName);
    }

    async Task<IReadOnlyList<ChargeView>> ChargesAsync(
        Guid transactionId, CurrencyCode walletCurrency, IReadOnlyList<TraceLineItem> lineItems, CancellationToken cancellationToken)
    {
        var charges = await db.Charges.AsNoTracking()
            .Where(charge => charge.TransactionId == transactionId)
            .ToListAsync(cancellationToken);

        return
        [
            .. charges
                .OrderBy(charge => charge.Currency)
                .Select(charge => ChargeViews.Of(charge, RolesAndAmounts(lineItems), walletCurrency)),
        ];
    }

    static IEnumerable<(EntryRole Role, Money Amount)> RolesAndAmounts(IEnumerable<TraceLineItem> items) =>
        items.Select(item => (item.Role, item.Amount));

    async Task<List<RevisionView>> HistoryAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var revisions = await db.TransactionRevisions.AsNoTracking()
            .Where(r => r.TransactionId == transactionId)
            .OrderBy(r => r.RevisionNumber)
            .ToListAsync(cancellationToken);

        var snapshots = revisions.Select(r => UnlessDamaged(() => RevisionSnapshotReader.Read(r.Snapshot))).ToList();
        var parsed = snapshots.Select(s => s.Value).OfType<ParsedSnapshot>().ToList();

        List<Guid> walletIds =
        [
            .. parsed
                .SelectMany(s => new[] { s.WalletId, s.Transfer?.FromWalletId, s.Transfer?.ToWalletId })
                .OfType<Guid>()
                .Distinct(),
        ];
        var wallets = await db.Wallets.AsNoTracking()
            .Where(w => walletIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, cancellationToken);

        List<string> slugs = [.. parsed.SelectMany(s => s.Items).Select(i => i.CategorySlug).OfType<string>().Distinct()];
        var categoryNames = await db.Categories.AsNoTracking()
            .Where(c => slugs.Contains(c.Slug))
            .ToDictionaryAsync(c => c.Slug, c => c.NameEn, cancellationToken);

        return
        [
            .. revisions.Select((r, index) =>
            {
                var (snapshot, unreadable) = snapshots[index] is { Value: { } read }
                    ? UnlessDamaged(() => ToView(read, wallets, categoryNames))
                    : (null, snapshots[index].Unreadable);
                return new RevisionView(
                    r.CreatedAt, r.Kind.ToString(), r.Instruction ?? $"{r.StatusBefore} → {r.StatusAfter}", snapshot, unreadable);
            }),
        ];
    }

    // The reader throws on a damaged snapshot rather than read it as zero, and one that parses can still hold amounts
    // no view can be built from (a fee as large as its leg leaves no rate). The trace page is where such a snapshot
    // would be investigated, so that revision is marked unreadable and everything else on the page still renders.
    static (T? Value, bool Unreadable) UnlessDamaged<T>(Func<T?> build) where T : class
    {
        try
        {
            return (build(), false);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or OverflowException or ArgumentException)
        {
            return (null, true);
        }
    }

    // Names are today's, resolved at read time; a snapshot keeps ids, not names.
    static RevisionSnapshotView ToView(
        ParsedSnapshot snapshot, IReadOnlyDictionary<Guid, Wallet> wallets, IReadOnlyDictionary<string, string> categoryNames)
    {
        List<TraceLineItem> items =
        [
            .. snapshot.Items.Select(item => new TraceLineItem(
                item.Description,
                item.Amount,
                item.CategorySlug is { } slug && categoryNames.TryGetValue(slug, out var name) ? name : null,
                item.Role)),
        ];
        var fee = items.Where(item => item.Role == EntryRole.Fee).Select(item => (Money?)item.Amount).FirstOrDefault();

        var transfer = snapshot.Transfer is { } legs
            ? new TransferLine(
                WalletName(legs.FromWalletId), legs.From, WalletName(legs.ToWalletId), legs.To, fee, legs.FeeLeg,
                TransferLines.RateOf(legs.From, legs.To, fee, legs.FeeLeg, legs.StatedRate, legs.StatedRateBase))
            : null;

        List<ChargeView> charges = snapshot.WalletId is { } recordWalletId && wallets.TryGetValue(recordWalletId, out var wallet)
            ? [.. snapshot.Charges.Select(charge => ChargeViews.Of(charge, RolesAndAmounts(items), wallet.Currency))]
            : [];

        return new RevisionSnapshotView(
            snapshot.Kind, snapshot.WalletId is { } ownWalletId ? WalletName(ownWalletId) : null, items, transfer, charges);

        string WalletName(Guid id) => wallets.TryGetValue(id, out var found) ? found.Name : id.ToString();
    }

    async Task<ReceiptTraceView?> ReceiptTraceAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.AsNoTracking()
            .SingleOrDefaultAsync(r => r.TransactionId == transactionId, cancellationToken);

        if (receipt is null)
            return null;

        var categoryByReceiptLineId = await (
            from lineItem in db.LineItems.AsNoTracking()
            where lineItem.TransactionId == transactionId && lineItem.ReceiptLineId != null
            join category in db.Categories.AsNoTracking() on lineItem.CategoryId equals category.Id into categoryJoin
            from category in categoryJoin.DefaultIfEmpty()
            select new { lineItem.ReceiptLineId, CategoryNameEn = category == null ? null : category.NameEn })
            .ToDictionaryAsync(row => row.ReceiptLineId!.Value, row => row.CategoryNameEn, cancellationToken);

        var receiptLines = await db.ReceiptLines.AsNoTracking()
            .Where(line => line.ReceiptId == receipt.Id)
            .OrderBy(line => line.Ordinal)
            .ToListAsync(cancellationToken);

        var lines = receiptLines
            .Select(line => new ReceiptTraceLine(
                line.Ordinal, line.Name, line.Quantity, line.Unit, line.UnitPrice, line.Total,
                categoryByReceiptLineId.TryGetValue(line.Id, out var categoryNameEn) ? categoryNameEn : null))
            .ToList();

        var awaitingConfirmation = await receiptStore.IsAwaitingConfirmationAsync(transactionId, cancellationToken);
        IReadOnlyList<string> problems = !awaitingConfirmation ? []
            : receipt.Kind == ReceiptKind.Exchange ? await SlipProblemsAsync(transactionId, cancellationToken)
            : BuildAwaitingConfirmationProblems(receiptLines, receipt.QrTotal ?? receipt.Total.Amount, receipt.Total.Currency);

        return new ReceiptTraceView(
            receipt.Source,
            receipt.SellerName,
            receipt.LocationName,
            receipt.SellerAddress,
            receipt.SellerTaxId,
            receipt.FiscalNumber,
            receipt.IssuedAt,
            receipt.PaymentMethod,
            receipt.Total.Amount,
            receipt.Total.Currency,
            receipt.QrTotal,
            lines,
            awaitingConfirmation,
            problems);
    }

    // The trace page's own, Persistence-local wording for the same mismatch RecordEcho separately
    // computes for the bot's confirmation prompt (Application, IRecordEcho.ComposeReceiptNeedsConfirmation)
    // - deliberately not shared across that assembly boundary, since Application grants Persistence no
    // InternalsVisibleTo today and this diagnostic sentence is not bot text. One definition here is
    // enough: nothing else in Persistence builds this sentence.
    static List<string> BuildAwaitingConfirmationProblems(List<ReceiptLine> lines, decimal referenceTotal, CurrencyCode currency)
    {
        var sum = lines.Sum(line => line.Total);
        if (Math.Abs(sum - referenceTotal) <= 0.01m)
            return [];

        return [$"Lines add up to {sum.ToString("0.00", CultureInfo.InvariantCulture)} {currency}, "
            + $"the receipt says {referenceTotal.ToString("0.00", CultureInfo.InvariantCulture)} {currency}"];
    }

    // A held slip's own reasons (ExtractedExchange.Assess), never the line-sum check, which a slip with no lines
    // would always trip. The PIB stored is vision's well-formed-only value, so a malformed one reads as unread.
    async Task<IReadOnlyList<string>> SlipProblemsAsync(Guid transactionId, CancellationToken cancellationToken) =>
        await receiptStore.GetExchangeSlipAsync(transactionId, cancellationToken) is { } slip
            ? [.. slip.Evidence.Assess(slip.SellerTaxId, taxIdMalformed: false).Problems.Select(DescribeSlipProblem)]
            : [];

    // Persistence-local wording, for BuildAwaitingConfirmationProblems' reason above; RecordEcho words the bot's
    // slip prompt the same way.
    static string DescribeSlipProblem(SlipProblem problem) => problem switch
    {
        SlipProblem.AmountsDisagree => "The given and received amounts don't match the printed rate",
        SlipProblem.TaxIdUnreadable => "The office's PIB is unreadable or not 9 digits",
        SlipProblem.SlipNumberUnreadable => "The slip number is unreadable, so a repeat of this slip can't be caught",
        _ => problem.ToString(),
    };

    static TraceEvent? ToTraceEvent(AppLogEntry entry)
    {
        using var properties = JsonDocument.Parse(entry.PropertiesJson!);
        if (!properties.RootElement.TryGetProperty(TransactionStages.StageProperty, out var stageElement)
            || stageElement.GetString() is not { } stage)
            return null;

        var eventId = properties.RootElement.TryGetProperty("EventId", out var eventIdElement)
            && eventIdElement.TryGetProperty("Id", out var idElement)
                ? idElement.GetInt32()
                : 0;

        var failedStage = properties.RootElement.TryGetProperty("FailedStage", out var failedStageElement)
            ? failedStageElement.GetString()
            : null;

        var reason = ExceptionReason.Extract(entry.Exception);

        return new TraceEvent(entry.LoggedAt, stage, eventId, entry.Level, entry.Message, entry.Exception, entry.PropertiesJson, failedStage, reason);
    }
}
