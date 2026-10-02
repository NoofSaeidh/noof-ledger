namespace Noof.Ledger.Domain;

// A Transfer transaction's own facts, as balance_checks holds a statement's. From is everything that left the
// source wallet and To everything that reached the destination, a fee inside the amount of its leg (T-12): what
// the bank or the purse shows, and what a statement will be reconciled against. The fee itself is a line item.
public sealed class Transfer
{
    public required Guid TransactionId { get; init; }
    public required Guid FromWalletId { get; set; }
    public required Money From { get; set; }
    public required Guid ToWalletId { get; set; }
    public required Money To { get; set; }
    public TransferLeg? FeeLeg { get; set; }

    // Kept only so the echo shows the rate the operator said (117.0000, never 116.9999 re-derived from rounded
    // amounts): quote units per one StatedRateBase. Both are null, or neither.
    public decimal? StatedRate { get; set; }
    public CurrencyCode? StatedRateBase { get; set; }

    public Guid? VenueMerchantId { get; set; }
}
