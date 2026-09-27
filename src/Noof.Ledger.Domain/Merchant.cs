namespace Noof.Ledger.Domain;

public sealed class Merchant
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; set; }
    public required MerchantKind Kind { get; set; }

    // A PIB (seller tax id) resolves the merchant with no model tokens. Write-once, like an alias:
    // set the first time a receipt names it, never overwritten.
    public string? TaxId { get; set; }
}
