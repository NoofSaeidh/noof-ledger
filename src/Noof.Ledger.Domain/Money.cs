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

    // Splits this amount in proportion to weights by the largest-remainder method: every exact share is cut toward zero
    // to the cent, then the cents left over go one at a time to the shares with the largest cut-off fractions (the first
    // on a tie), so the parts add up to Amount exactly and a share never takes a sign its weight does not have - a
    // discount line's negative weight keeps a negative share. Only the weights' sum has to be above zero.
    public IReadOnlyList<Money> Allocate(IReadOnlyList<decimal> weights)
    {
        var total = weights.Sum();
        if (weights.Count == 0 || total <= 0)
            throw new ArgumentException("Weights must be one or more and sum above zero.", nameof(weights));

        var amount = Amount;
        var currency = Currency;

        // Multiply before dividing: a ratio like 1/90 rounded to decimal's 28 digits would cut an exact half-cent
        // share just under its true fraction and lose a tie it should win.
        var shares = weights.Select(weight => amount * weight / total).ToArray();
        var parts = shares.Select(share => Math.Round(share, 2, MidpointRounding.ToZero)).ToArray();

        var leftover = amount - parts.Sum();
        var cent = leftover < 0 ? -0.01m : 0.01m;
        var takers = Enumerable.Range(0, parts.Length)
            .OrderByDescending(index => (shares[index] - parts[index]) / cent)
            .Take((int)(leftover / cent))
            .ToList();
        foreach (var index in takers)
            parts[index] += cent;

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
