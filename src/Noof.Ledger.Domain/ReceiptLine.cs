namespace Noof.Ledger.Domain;

// Evidence, in the receipt's own order (1-based Ordinal). line_items.receipt_line_id points back
// here once the categorisation worker builds the transaction's line items from these rows.
public sealed class ReceiptLine
{
    public required Guid Id { get; init; }
    public required Guid ReceiptId { get; init; }
    public required int Ordinal { get; init; }
    public required string Name { get; init; }
    public required decimal Quantity { get; init; }
    public string? Unit { get; init; }
    public required decimal UnitPrice { get; init; }
    public required decimal Total { get; init; }
    public string? TaxLabel { get; init; }
}
