namespace Noof.Ledger.Domain;

public enum TransactionStatus
{
    Captured = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
}
