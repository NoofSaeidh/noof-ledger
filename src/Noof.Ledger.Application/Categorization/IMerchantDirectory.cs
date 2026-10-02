namespace Noof.Ledger.Application.Categorization;

public interface IMerchantDirectory
{
    Task<IReadOnlyList<MerchantAliasEntry>> AliasesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MerchantOption>> MerchantsAsync(CancellationToken cancellationToken);

    // Write-once. Creates a merchant row with displayName and inserts the alias keyed on folded.
    // If another worker inserted the same folded key first, returns THAT merchant's id and writes
    // nothing — the table decides identity, not whoever got there second.
    Task<Guid> LinkAliasAsync(string folded, string displayName, CancellationToken cancellationToken);

    // A PIB hit resolves the merchant with no model tokens (Phase 6).
    Task<Guid?> FindByTaxIdAsync(string taxId, CancellationToken cancellationToken);

    // Write-once, like an alias: sets merchantId's TaxId only if it has none yet. A no-op when the
    // merchant already carries a (necessarily identical, by the unique index) tax id, or when
    // another merchant claimed this one first.
    Task LinkTaxIdAsync(Guid merchantId, string taxId, CancellationToken cancellationToken);

    // The office a slip names, keyed by its PIB as a shop is (spec §3). One PIB is one legal entity
    // (A-9): a PIB a shop receipt already linked resolves to that merchant, whatever its kind; an
    // unknown one creates an ExchangeVenue carrying it.
    Task<Guid> VenueForTaxIdAsync(string taxId, string displayName, CancellationToken cancellationToken);
}
