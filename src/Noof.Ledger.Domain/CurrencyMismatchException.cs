namespace Noof.Ledger.Domain;

public sealed class CurrencyMismatchException(CurrencyCode left, CurrencyCode right)
    : InvalidOperationException($"{left} and {right} are different currencies.");
