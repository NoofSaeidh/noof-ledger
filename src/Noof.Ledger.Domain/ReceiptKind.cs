namespace Noof.Ledger.Domain;

// Mirrors Noof.Ledger.Application.Receipts.ReceiptKind value for value - see ReceiptSource.
public enum ReceiptKind
{
    Sale = 0,
    Refund = 1,
    Copy = 2,
    Training = 3,
    Proforma = 4,
    Advance = 5,
}
