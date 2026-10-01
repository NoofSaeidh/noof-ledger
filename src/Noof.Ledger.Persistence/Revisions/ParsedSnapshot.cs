using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Revisions;

internal sealed record ParsedSnapshot(
    TransactionKind? Kind, Guid? WalletId, IReadOnlyList<ParsedItem> Items, ParsedTransfer? Transfer, IReadOnlyList<ParsedCharge> Charges);

internal sealed record ParsedItem(string Description, Money Amount, string? CategorySlug, EntryRole Role);

internal sealed record ParsedTransfer(
    Guid FromWalletId, Money From, Guid ToWalletId, Money To, TransferLeg? FeeLeg, decimal? StatedRate,
    CurrencyCode? StatedRateBase, Guid? VenueMerchantId);
