namespace Noof.Ledger.Domain;

public readonly record struct Money(decimal Amount, CurrencyCode Currency) : IComparable<Money>, IComparable
{
    public static Money operator +(Money left, Money right)
    {
        RequireSameCurrency(left, right);

        return left with { Amount = left.Amount + right.Amount };
    }

    public static Money operator -(Money left, Money right)
    {
        RequireSameCurrency(left, right);

        return left with { Amount = left.Amount - right.Amount };
    }

    public static Money operator -(Money value)
    {
        RequireCurrency(value);

        return value with { Amount = -value.Amount };
    }

    public int CompareTo(Money other)
    {
        RequireSameCurrency(this, other);

        return Amount.CompareTo(other.Amount);
    }

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        Money other => CompareTo(other),
        _ => throw new ArgumentException($"Cannot compare money with {obj.GetType().Name}.", nameof(obj)),
    };

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    static void RequireCurrency(Money money)
    {
        if (money.Currency.Value is null)
            throw new InvalidOperationException("Money without a currency cannot be negated, combined or compared.");
    }

    static void RequireSameCurrency(Money left, Money right)
    {
        RequireCurrency(left);
        RequireCurrency(right);

        if (left.Currency != right.Currency)
            throw new CurrencyMismatchException(left.Currency, right.Currency);
    }

    public override string ToString() => $"{Amount} {Currency}";
}
