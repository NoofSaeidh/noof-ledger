using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Revisions;

// Called inside the caller's database transaction, after the change itself is saved, so the snapshot is read
// back from the rows exactly as they will commit. The caller holds the transaction's row lock, which is what
// makes max + 1 safe; the unique index is the backstop.
internal static class RevisionLog
{
    public static async Task AppendAsync(
        LedgerDbContext db, Transaction transaction, RevisionKind kind, string? instruction,
        TransactionStatus statusBefore, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var last = await db.TransactionRevisions
            .Where(r => r.TransactionId == transaction.Id)
            .MaxAsync(r => (int?)r.RevisionNumber, cancellationToken) ?? 0;

        db.TransactionRevisions.Add(new TransactionRevision
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            RevisionNumber = last + 1,
            Kind = kind,
            Instruction = instruction,
            StatusBefore = statusBefore,
            StatusAfter = transaction.Status,
            Snapshot = await SnapshotAsync(db, transaction, cancellationToken),
            CreatedAt = now,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    static async Task<string> SnapshotAsync(LedgerDbContext db, Transaction transaction, CancellationToken cancellationToken)
    {
        var lines = await (
            from li in db.LineItems.AsNoTracking()
            where li.TransactionId == transaction.Id
            join c in db.Categories.AsNoTracking() on li.CategoryId equals c.Id into categoryJoin
            from c in categoryJoin.DefaultIfEmpty()
            orderby li.Ordinal
            select new { li.Description, li.Amount, CategorySlug = c == null ? null : c.Slug, li.MerchantId, li.CategorizedBy, li.Role })
            .ToListAsync(cancellationToken);

        var checkpoint = await db.BalanceChecks.AsNoTracking()
            .Where(check => check.TransactionId == transaction.Id)
            .Select(check => new { check.Stated.Amount, check.Stated.Currency })
            .SingleOrDefaultAsync(cancellationToken);

        var transfer = await db.Transfers.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TransactionId == transaction.Id, cancellationToken);

        // Amounts and rates are written as decimal strings, never JSON numbers, so no reader of this history can take
        // them as floating point.
        return JsonSerializer.Serialize(new Snapshot(
            transaction.RawText,
            transaction.OccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [.. lines.Select(line => new SnapshotLine(
                line.Description,
                line.Amount.Amount.ToString(CultureInfo.InvariantCulture),
                line.Amount.Currency.Value,
                line.CategorySlug,
                line.MerchantId,
                (int)line.CategorizedBy,
                (int)line.Role))],
            transaction.Kind.ToString(),
            transaction.WalletId,
            checkpoint is null
                ? null
                : new SnapshotBalance(checkpoint.Amount.ToString(CultureInfo.InvariantCulture), checkpoint.Currency.Value),
            transfer is null ? null : SnapshotTransferOf(transfer)));
    }

    static SnapshotTransfer SnapshotTransferOf(Transfer transfer) => new(
        transfer.FromWalletId,
        transfer.From.Amount.ToString(CultureInfo.InvariantCulture),
        transfer.From.Currency.Value,
        transfer.ToWalletId,
        transfer.To.Amount.ToString(CultureInfo.InvariantCulture),
        transfer.To.Currency.Value,
        (int?)transfer.FeeLeg,
        transfer.StatedRate?.ToString(CultureInfo.InvariantCulture),
        transfer.StatedRateBase?.Value,
        transfer.VenueMerchantId);

    sealed record Snapshot(
        [property: JsonPropertyName("raw_text")] string? RawText,
        [property: JsonPropertyName("occurred_on")] string OccurredOn,
        [property: JsonPropertyName("items")] IReadOnlyList<SnapshotLine> Items,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("wallet_id")] Guid? WalletId,
        [property: JsonPropertyName("stated_balance")] SnapshotBalance? StatedBalance,
        [property: JsonPropertyName("transfer")] SnapshotTransfer? Transfer);

    sealed record SnapshotLine(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("amount")] string Amount,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("category_slug")] string? CategorySlug,
        [property: JsonPropertyName("merchant_id")] Guid? MerchantId,
        [property: JsonPropertyName("categorized_by")] int CategorizedBy,
        [property: JsonPropertyName("role")] int Role);

    sealed record SnapshotBalance(
        [property: JsonPropertyName("amount")] string Amount,
        [property: JsonPropertyName("currency")] string Currency);

    sealed record SnapshotTransfer(
        [property: JsonPropertyName("from_wallet_id")] Guid FromWalletId,
        [property: JsonPropertyName("from_amount")] string FromAmount,
        [property: JsonPropertyName("from_currency")] string FromCurrency,
        [property: JsonPropertyName("to_wallet_id")] Guid ToWalletId,
        [property: JsonPropertyName("to_amount")] string ToAmount,
        [property: JsonPropertyName("to_currency")] string ToCurrency,
        [property: JsonPropertyName("fee_leg")] int? FeeLeg,
        [property: JsonPropertyName("stated_rate")] string? StatedRate,
        [property: JsonPropertyName("stated_rate_base")] string? StatedRateBase,
        [property: JsonPropertyName("venue_merchant_id")] Guid? VenueMerchantId);
}
