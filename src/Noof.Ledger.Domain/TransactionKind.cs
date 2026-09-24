namespace Noof.Ledger.Domain;

// Stored as an integer; 3 is kept for Phase 7's Transfer and is not declared until then.
public enum TransactionKind
{
    Expense = 0,
    Income = 1,
    BalanceCheck = 2,
}
