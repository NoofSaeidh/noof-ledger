namespace Noof.Ledger.Domain;

// Evidence: never edited after insert. A correction changes line items and writes a revision, as
// today - it never rewrites a receipt row.
public sealed class Receipt
{
    public required Guid Id { get; init; }
    public required Guid TransactionId { get; init; }
    public required ReceiptSource Source { get; init; }
    public string? VerificationUrl { get; init; }
    public string? SellerTaxId { get; init; }
    public string? SellerName { get; init; }
    public string? SellerAddress { get; init; }
    public string? LocationName { get; init; }
    public string? FiscalNumber { get; init; }
    public DateTimeOffset? IssuedAt { get; init; }
    public required Money Total { get; init; }
    public required ReceiptKind Kind { get; init; }
    public PaymentMethod? PaymentMethod { get; init; }
    public decimal? QrTotal { get; init; }
    public string? TelegramFileId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
