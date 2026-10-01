using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

public interface IProposalMapper
{
    // failure is set on every failure - the job's error text. reason is set only on a failure a transfer's own rules
    // name (spec §2), and then failure is reason.ToString(); it is None on every other failure and on success.
    bool TryMap(
        CategorizationProposal proposal,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        IReadOnlyList<WalletOption> wallets,
        string defaultCurrency,
        out MappedProposal mapped,
        out string failure,
        out RecordFailureReason reason);
}
