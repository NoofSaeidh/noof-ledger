namespace Noof.Ledger.Domain;

// What a spending's lines in one foreign currency cost its wallet, in the wallet's currency. A row freezes the
// record: changing the wallet's terms never reprices it, and the fee terms here are the snapshot it was computed
// with. ChargedAmount excludes the fee, as the operator's banks post the commission as its own transaction.
public sealed class Charge
{
    public required Guid TransactionId { get; init; }
    public required CurrencyCode Currency { get; init; }
    public required decimal ChargedAmount { get; set; }

    // Zero when none. A fee line is replaced on every apply, so a stated fee survives a correction only here.
    public required decimal FeeAmount { get; set; }

    public required decimal RateUsed { get; set; }
    public decimal? FeePercent { get; set; }
    public decimal? FeeFixed { get; set; }
    public decimal? FeeMinimum { get; set; }
    public required ChargeSource Source { get; set; }
}
