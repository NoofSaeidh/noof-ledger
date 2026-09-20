namespace Noof.Ledger.Domain;

public sealed class CurrencyMismatchException(CurrencyCode left, CurrencyCode right)
    : InvalidOperationException($"Cannot combine {left} with {right}.");
