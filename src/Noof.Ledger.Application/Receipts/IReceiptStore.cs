using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Receipts;

// Null when the receipt saved cleanly (ReceiptId is then set); a duplicate seller+fiscal-number
// pair writes nothing and names the transaction that already recorded it instead.
public sealed record ReceiptSaveResult(Guid? ReceiptId, Guid? DuplicateOfTransactionId);

public sealed record ReceiptLineView(
    Guid Id, int Ordinal, string Name, decimal Quantity, string? Unit, decimal UnitPrice, decimal Total, string? TaxLabel);

public sealed record ReceiptView(
    Guid Id,
    ReceiptSource Source,
    string? SellerTaxId,
    string? SellerName,
    string? SellerAddress,
    string? LocationName,
    string? FiscalNumber,
    DateTimeOffset? IssuedAt,
    decimal Total,
    CurrencyCode Currency,
    ReceiptKind Kind,
    PaymentMethod? PaymentMethod,
    decimal? QrTotal,
    string? VerificationUrl,
    IReadOnlyList<ReceiptLineView> Lines);

public interface IReceiptStore
{
    // Inserts the receipt and its lines in one database transaction. A duplicate (seller_tax_id +
    // fiscal_number already recorded for another transaction) writes nothing and names that
    // transaction - detected through the unique index, not a racy pre-check alone.
    Task<ReceiptSaveResult> SaveExtractedAsync(
        Guid transactionId, ExtractedReceipt receipt, string? telegramFileId, CancellationToken cancellationToken);

    Task<ReceiptView?> GetByTransactionAsync(Guid transactionId, CancellationToken cancellationToken);

    // The capture's own Telegram file id (set by CaptureReceiptAsync), for ExtractReceiptWorker to
    // download the photo again - the photo itself is never stored (R-4).
    Task<string?> GetTelegramFileIdAsync(Guid transactionId, CancellationToken cancellationToken);

    // The capture's own fiscal QR link, set by CaptureReceiptAsync for a text-link capture (never set
    // together with a Telegram file id - CapturedReceipt carries exactly one of the two).
    Task<string?> GetVerificationUrlAsync(Guid transactionId, CancellationToken cancellationToken);
}
