namespace Noof.Ledger.Domain;

// An exchange-office slip exactly as vision read it, each field null when unread: evidence, written once and
// never updated. The record's money is its transfers row (T-8), which a reply may correct; this never changes.
public sealed class ReceiptExchange
{
    public required Guid ReceiptId { get; init; }
    public decimal? GivenAmount { get; init; }
    public string? GivenCurrency { get; init; }
    public decimal? ReceivedAmount { get; init; }
    public string? ReceivedCurrency { get; init; }

    // Dinars per one foreign unit, as a Serbian slip prints it.
    public decimal? Rate { get; init; }

    public decimal? CommissionAmount { get; init; }
    public string? CommissionCurrency { get; init; }
    public string? SlipNumber { get; init; }
}
