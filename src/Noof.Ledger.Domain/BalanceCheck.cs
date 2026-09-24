namespace Noof.Ledger.Domain;

// A balance statement is a checkpoint, not an adjustment entry (M6). ComputedBefore is history for the echo, in the
// stated currency; nothing reads it back as a balance.
public sealed class BalanceCheck
{
    public required Guid TransactionId { get; init; }
    public required Guid WalletId { get; set; }
    public required Money Stated { get; set; }
    public required decimal ComputedBefore { get; set; }
}
