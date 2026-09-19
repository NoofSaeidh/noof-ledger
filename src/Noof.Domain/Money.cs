namespace Noof.Domain;

public readonly record struct Money(decimal Amount, CurrencyCode Currency)
{
    public static Money operator +(Money left, Money right) =>
        left.Currency == right.Currency
            ? left with { Amount = left.Amount + right.Amount }
            : throw new CurrencyMismatchException(left.Currency, right.Currency);

    public static Money operator -(Money left, Money right) =>
        left.Currency == right.Currency
            ? left with { Amount = left.Amount - right.Amount }
            : throw new CurrencyMismatchException(left.Currency, right.Currency);

    public static Money operator -(Money value) => value with { Amount = -value.Amount };

    public override string ToString() => $"{Amount} {Currency}";
}
