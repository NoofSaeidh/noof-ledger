namespace Noof.Ledger.Application.Categorization;

public interface ICategorizer
{
    Task<CategorizationProposal> ProposeAsync(CategorizationRequest request, CancellationToken cancellationToken);

    // Called ONLY when a merchant the model quoted is absent from the alias table. knownMerchants
    // is the full canonical list, so the model can answer with an existing spelling instead of
    // minting a near-duplicate. Returns the display name to store.
    Task<string> CanonicalizeMerchantAsync(
        string merchantText, IReadOnlyList<MerchantOption> knownMerchants, CancellationToken cancellationToken);
}
