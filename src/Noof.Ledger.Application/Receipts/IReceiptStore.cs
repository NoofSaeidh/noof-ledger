using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Receipts;

// Null when the receipt saved cleanly, or was already saved for this same transaction by an earlier,
// replayed attempt (ReceiptId is then the existing row's id either way). A duplicate
// seller+fiscal-number pair (or, for an exchange slip, seller+slip-number) recorded for ANOTHER
// transaction writes nothing and names that transaction instead - ReceiptId is null and
// DuplicateOfTransactionId never equals the transaction being saved.
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

// An exchange-office slip as saved (spec §3): SlipNumber is the normalised one the duplicate guard keys on,
// Evidence the receipt_exchanges row exactly as vision read it. IssuedAt is the stored UTC instant.
public sealed record ExchangeSlipView(
    Guid ReceiptId, string? SellerTaxId, string? SellerName, DateTimeOffset? IssuedAt, string? SlipNumber, ExtractedExchange Evidence);

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

    // An exchange-office slip (spec §3): the receipts row (Kind = Exchange, the slip number trimmed and
    // upper-cased) and its receipt_exchanges evidence, plus what the disposition asks for, in ONE commit -
    // Record queues RecordExchange and then the caption's Correct job, Hold queues nothing (the caption waits
    // for "Record anyway"), Incomplete fails the record with SlipIncomplete and queues the caption at once - a record
    // already Cancelled stays Cancelled but keeps SlipIncomplete, so Restore brings it back Failed. A
    // (seller_tax_id, slip_number) another transaction already recorded writes nothing and names it, as
    // SaveExtractedAsync does for a fiscal receipt; a replay of this transaction's own save returns its id.
    Task<ReceiptSaveResult> SaveExchangeSlipAsync(
        Guid transactionId, ExtractedReceipt receipt, ExtractedExchange exchange, string? telegramFileId,
        SlipDisposition disposition, CancellationToken cancellationToken);

    // Null for a transaction with no Exchange receipt.
    Task<ExchangeSlipView?> GetExchangeSlipAsync(Guid transactionId, CancellationToken cancellationToken);

    Task<ReceiptView?> GetByTransactionAsync(Guid transactionId, CancellationToken cancellationToken);

    // The capture's own Telegram file id (set by CaptureReceiptAsync), for ExtractReceiptWorker to
    // download the photo again - the photo itself is never stored (R-4).
    Task<string?> GetTelegramFileIdAsync(Guid transactionId, CancellationToken cancellationToken);

    // The capture's own fiscal QR link, set by CaptureReceiptAsync for a text-link capture (never set
    // together with a Telegram file id - CapturedReceipt carries exactly one of the two).
    Task<string?> GetVerificationUrlAsync(Guid transactionId, CancellationToken cancellationToken);

    // The operator's own "Record anyway" on a vision receipt saved without its job: CategorizeReceipt for a
    // fiscal receipt, RecordExchange and then the caption's Correct job for a held exchange slip.
    // sourceMessageId is the echo's own Telegram message id, so the same unique index (transaction_id,
    // source_message_id, kind) that already makes a redelivered correction a no-op makes a second press of the
    // same button a no-op here too - false, not an exception, is "already queued". The transaction must still be
    // Captured and still awaiting confirmation at insert time, checked under the same row lock the insert runs
    // under (2026-09-27): a Cancel that lands between a caller's own status read and this call must never still
    // get a job queued - false covers that case too, and the caller treats it exactly like "already queued".
    // A caller that gets false just re-renders the record's current state.
    Task<bool> EnqueueCategorizationAsync(Guid transactionId, int sourceMessageId, CancellationToken cancellationToken);

    // A fiscal vision receipt that has never had a CategorizeReceipt job, or (A-7) a vision exchange
    // slip that was never recorded - no RecordExchange job, no Initial/Correction/Edit revision - and is not
    // failed. Independent of the transaction's own current status, so it reads the same right after Cancel as
    // before either button is pressed: RecordActionHandler keeps showing the confirmation prompt through
    // Cancel/Restore, EfTransactionTrace shows the same value on the trace page, and ExtractReceiptWorker's
    // lease-expiry replay uses it in place of recomputing why the receipt was held
    // (docs/decisions/p6-2-vision-fallback-stopped-inventing-receipts.md).
    Task<bool> IsAwaitingConfirmationAsync(Guid transactionId, CancellationToken cancellationToken);
}
