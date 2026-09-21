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
}
