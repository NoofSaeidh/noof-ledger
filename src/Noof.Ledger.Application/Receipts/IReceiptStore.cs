using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Receipts;

// Null when the receipt saved cleanly, or was already saved for this same transaction by an earlier,
// replayed attempt (ReceiptId is then the existing row's id either way, C-1). A duplicate
// seller+fiscal-number pair recorded for ANOTHER transaction writes nothing and names that
// transaction instead - ReceiptId is null and DuplicateOfTransactionId never equals the transaction
// being saved.
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
    // enqueueCategorization is false only for a vision receipt ExtractReceiptWorker itself judged not
    // to add up (2026-09-27): the receipt and its lines are still saved so the echo can show exactly
    // what was read, but CategorizeReceipt waits for the operator's own "Record anyway".
    Task<ReceiptSaveResult> SaveExtractedAsync(
        Guid transactionId, ExtractedReceipt receipt, string? telegramFileId, bool enqueueCategorization,
        CancellationToken cancellationToken);

    Task<ReceiptView?> GetByTransactionAsync(Guid transactionId, CancellationToken cancellationToken);

    // The capture's own Telegram file id (set by CaptureReceiptAsync), for ExtractReceiptWorker to
    // download the photo again - the photo itself is never stored (R-4).
    Task<string?> GetTelegramFileIdAsync(Guid transactionId, CancellationToken cancellationToken);

    // The capture's own fiscal QR link, set by CaptureReceiptAsync for a text-link capture (never set
    // together with a Telegram file id - CapturedReceipt carries exactly one of the two).
    Task<string?> GetVerificationUrlAsync(Guid transactionId, CancellationToken cancellationToken);

    // The operator's own "Record anyway" on a receipt SaveExtractedAsync saved without enqueueing
    // CategorizeReceipt. sourceMessageId is the echo's own Telegram message id, so the same unique
    // index (transaction_id, source_message_id, kind) that already makes a redelivered correction a
    // no-op makes a second press of the same button a no-op here too - false, not an exception, is
    // "already queued".
    Task<bool> EnqueueCategorizationAsync(Guid transactionId, int sourceMessageId, CancellationToken cancellationToken);
}
