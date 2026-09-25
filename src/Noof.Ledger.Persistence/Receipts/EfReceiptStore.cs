using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Npgsql;
using AppReceipts = Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Persistence.Receipts;

// Application.Receipts and Domain each declare their own ReceiptSource/ReceiptKind/PaymentMethod
// (Domain has zero NuGet references and cannot depend on Application - see ReceiptSource.cs), kept
// in step by ReceiptEnumTests. This class is the one place that casts between them.
internal sealed class EfReceiptStore(LedgerDbContext db, TimeProvider timeProvider) : AppReceipts.IReceiptStore
{
    public async Task<AppReceipts.ReceiptSaveResult> SaveExtractedAsync(
        Guid transactionId, AppReceipts.ExtractedReceipt receipt, string? telegramFileId, CancellationToken cancellationToken)
    {
        var receiptId = Guid.NewGuid();

        var row = new Receipt
        {
            Id = receiptId,
            TransactionId = transactionId,
            Source = (ReceiptSource)receipt.Source,
            VerificationUrl = receipt.VerificationUrl,
            SellerTaxId = receipt.SellerTaxId,
            SellerName = receipt.SellerName,
            SellerAddress = receipt.SellerAddress,
            LocationName = receipt.LocationName,
            FiscalNumber = receipt.FiscalNumber,
            IssuedAt = receipt.IssuedAt,
            Total = new Money(receipt.Total, receipt.Currency),
            Kind = (ReceiptKind)receipt.Kind,
            PaymentMethod = (PaymentMethod?)receipt.PaymentMethod,
            QrTotal = receipt.QrTotal,
            TelegramFileId = telegramFileId,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        db.Receipts.Add(row);

        foreach (var line in receipt.Lines)
        {
            db.ReceiptLines.Add(new ReceiptLine
            {
                Id = Guid.NewGuid(),
                ReceiptId = receiptId,
                Ordinal = line.Ordinal,
                Name = line.Name,
                Quantity = line.Quantity,
                Unit = line.Unit,
                UnitPrice = line.UnitPrice,
                Total = line.Total,
                TaxLabel = line.TaxLabel,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new AppReceipts.ReceiptSaveResult(receiptId, null);
        }
        catch (DbUpdateException ex) when (IsDuplicateReceiptViolation(ex))
        {
            db.ChangeTracker.Clear();

            var duplicateOf = await db.Receipts.AsNoTracking()
                .Where(r => r.SellerTaxId == receipt.SellerTaxId && r.FiscalNumber == receipt.FiscalNumber)
                .Select(r => (Guid?)r.TransactionId)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "A unique-constraint violation on receipts reported a duplicate that cannot be found.");

            return new AppReceipts.ReceiptSaveResult(null, duplicateOf);
        }
    }

    public async Task<AppReceipts.ReceiptView?> GetByTransactionAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.AsNoTracking()
            .SingleOrDefaultAsync(r => r.TransactionId == transactionId, cancellationToken);

        if (receipt is null)
            return null;

        var lines = await db.ReceiptLines.AsNoTracking()
            .Where(l => l.ReceiptId == receipt.Id)
            .OrderBy(l => l.Ordinal)
            .Select(l => new AppReceipts.ReceiptLineView(l.Id, l.Ordinal, l.Name, l.Quantity, l.Unit, l.UnitPrice, l.Total, l.TaxLabel))
            .ToListAsync(cancellationToken);

        return new AppReceipts.ReceiptView(
            receipt.Id,
            (AppReceipts.ReceiptSource)receipt.Source,
            receipt.SellerTaxId,
            receipt.SellerName,
            receipt.SellerAddress,
            receipt.LocationName,
            receipt.FiscalNumber,
            receipt.IssuedAt,
            receipt.Total.Amount,
            receipt.Total.Currency,
            (AppReceipts.ReceiptKind)receipt.Kind,
            (AppReceipts.PaymentMethod?)receipt.PaymentMethod,
            receipt.QrTotal,
            receipt.VerificationUrl,
            lines);
    }

    public Task<string?> GetTelegramFileIdAsync(Guid transactionId, CancellationToken cancellationToken) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.Id == transactionId)
            .Select(t => t.TelegramFileId)
            .SingleOrDefaultAsync(cancellationToken);

    static bool IsDuplicateReceiptViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ReceiptConfiguration.DuplicateIndex,
        };
}
