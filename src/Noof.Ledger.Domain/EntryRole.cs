namespace Noof.Ledger.Domain;

// Also the type of LineItem.Role: a fee line and the fee entry it posts share one vocabulary.
public enum EntryRole
{
    Principal = 0,
    Fee = 1,
}
