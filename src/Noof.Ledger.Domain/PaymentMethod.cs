namespace Noof.Ledger.Domain;

// Mirrors Noof.Ledger.Application.Receipts.PaymentMethod value for value - see ReceiptSource.
public enum PaymentMethod
{
    Card = 0,
    Cash = 1,
    Transfer = 2,
    Voucher = 3,
    Other = 4,
    Mixed = 5,
}
