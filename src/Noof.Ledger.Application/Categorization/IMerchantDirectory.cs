namespace Noof.Ledger.Application.Categorization;

public interface IMerchantDirectory
{
    Task<IReadOnlyList<MerchantAliasEntry>> AliasesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MerchantOption>> MerchantsAsync(CancellationToken cancellationToken);

    // Write-once. Creates a merchant row with displayName and inserts the alias keyed on folded.
    // If another worker inserted the same folded key first, returns THAT merchant's id and writes
    // nothing — the table decides identity, not whoever got there second.
    Task<Guid> LinkAliasAsync(string folded, string displayName, CancellationToken cancellationToken);
}
