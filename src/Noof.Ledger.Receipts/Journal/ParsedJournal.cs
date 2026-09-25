using Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Receipts.Journal;

internal sealed record ParsedJournal(
    string? SellerTaxId,
    string? SellerName,
    string? SellerAddress,
    string? LocationName,
    string? FiscalNumber,
    DateTimeOffset? IssuedAt,
    decimal Total,
    PaymentMethod? PaymentMethod,
    IReadOnlyList<ExtractedReceiptLine> Lines);
