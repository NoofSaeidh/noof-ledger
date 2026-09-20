namespace Noof.Ledger.Domain;

public readonly record struct Money(decimal Amount, CurrencyCode Currency)
{
    public static Money operator +(Money left, Money right)
    {
        RequireCurrency(left);
        RequireCurrency(right);

        return left.Currency == right.Currency
            ? left with { Amount = left.Amount + right.Amount }
            : throw new CurrencyMismatchException(left.Currency, right.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        RequireCurrency(left);
        RequireCurrency(right);

        return left.Currency == right.Currency
            ? left with { Amount = left.Amount - right.Amount }
            : throw new CurrencyMismatchException(left.Currency, right.Currency);
    }

    public static Money operator -(Money value) => value with { Amount = -value.Amount };

    static void RequireCurrency(Money money)
    {
        if (money.Currency.Value is null)
            throw new InvalidOperationException("Money without a currency cannot be combined.");
    }

    public override string ToString() => $"{Amount} {Currency}";
}
