namespace Noof.Ledger.Application.Categorization;

public interface IProposalMapper
{
    bool TryMap(
        CategorizationProposal proposal,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        IReadOnlyList<WalletOption> wallets,
        string defaultCurrency,
        out MappedProposal mapped,
        out string failure);
}
