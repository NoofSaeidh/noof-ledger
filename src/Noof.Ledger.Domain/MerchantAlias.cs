namespace Noof.Ledger.Domain;

// Write-once: an alias is folded once and never edited, so there is no
// UpdatedAt column to maintain.
public sealed class MerchantAlias
{
    // The primary key is the folded text itself, not a synthetic Guid - every
    // other entity in this file gets a Guid Id, this one deliberately does not.
    public required string Folded { get; init; }
    public required Guid MerchantId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
