namespace Noof.Ledger.Domain;

public sealed class Merchant
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; set; }
    public required MerchantKind Kind { get; set; }
}
