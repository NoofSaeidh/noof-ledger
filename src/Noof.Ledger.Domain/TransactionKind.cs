namespace Noof.Ledger.Domain;

public enum TransactionKind
{
    Expense = 0,
    Income = 1,
    BalanceCheck = 2,
    Transfer = 3,
}
