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

    public Money Round() => this with { Amount = MoneyMath.Round(Amount) };

    // Splits this amount in proportion to weights: every part but one is rounded by MoneyMath.Round, and the part of
    // the largest weight (the first on a tie) takes the remainder, so the parts add up to Amount exactly. A negative
    // weight - a discount line - takes a negative part; only the sum has to be above zero.
    public IReadOnlyList<Money> Allocate(IReadOnlyList<decimal> weights)
    {
        var total = weights.Sum();
        if (weights.Count == 0 || total <= 0)
            throw new ArgumentException("Weights must be one or more and sum above zero.", nameof(weights));

        var largest = 0;
        for (var index = 1; index < weights.Count; index++)
            if (weights[index] > weights[largest])
                largest = index;

        var amount = Amount;
        var currency = Currency;

        // The ratio first: Amount × weight could overflow decimal where Amount × (weight / total) cannot.
        var parts = weights.Select(weight => MoneyMath.Round(amount * (weight / total))).ToArray();
        parts[largest] = amount - parts.Where((_, index) => index != largest).Sum();

        return [.. parts.Select(part => new Money(part, currency))];
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
