namespace Noof.Ledger.Domain;

public sealed class LineItem
{
    public required Guid Id { get; init; }
    public required Guid TransactionId { get; init; }
    public required string Description { get; set; }
    public required Money Amount { get; set; }
    public Guid? CategoryId { get; set; }
    public required CategorizationAuthority CategorizedBy { get; set; }
    public Guid? MerchantId { get; set; }

    // Order within the transaction (1-based): the order the message named the items in, or the
    // receipt's own order when ReceiptLineId is set. The echo and every read model order by this,
    // never by Description (BACKLOG "Line items keep no order").
    public required int Ordinal { get; set; }

    // Set once the categorisation worker resolves this line from a receipt's own lines (Phase 6).
    public Guid? ReceiptLineId { get; set; }
}
