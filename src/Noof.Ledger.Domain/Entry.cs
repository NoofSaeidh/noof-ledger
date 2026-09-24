namespace Noof.Ledger.Domain;

// Signed: an expense's entries are negative, an income's positive. A wallet's balance is derived from these and
// its checkpoints; nothing stores a running balance.
public sealed class Entry
{
    public required Guid Id { get; init; }
    public required Guid TransactionId { get; init; }
    public required Guid WalletId { get; init; }
    public required Money Amount { get; init; }
    public required EntryRole Role { get; init; }
}
