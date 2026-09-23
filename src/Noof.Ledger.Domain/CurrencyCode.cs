namespace Noof.Ledger.Domain;

public readonly record struct CurrencyCode : IComparable<CurrencyCode>, IComparable
{
    public CurrencyCode(string value)
    {
        if (value is not { Length: 3 } || !value.All(char.IsAsciiLetter))
            throw new ArgumentException($"'{value}' is not a three-letter ISO 4217 code.", nameof(value));

        Value = value.ToUpperInvariant();
    }

    public string Value { get; }

    public static readonly CurrencyCode Eur = new("EUR");
    public static readonly CurrencyCode Rsd = new("RSD");
    public static readonly CurrencyCode Usd = new("USD");
    public static readonly CurrencyCode Rub = new("RUB");
    public static readonly CurrencyCode Kzt = new("KZT");

    // Declared after the five codes on purpose: static fields initialise in textual order, so a list
    // declared above them would hold five default codes with a null Value.
    public static readonly IReadOnlyList<CurrencyCode> Supported = [Eur, Rsd, Usd, Rub, Kzt];

    public int CompareTo(CurrencyCode other) => string.CompareOrdinal(Value, other.Value);

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        CurrencyCode other => CompareTo(other),
        _ => throw new ArgumentException($"Cannot compare a currency code with {obj.GetType().Name}.", nameof(obj)),
    };

    public static bool operator <(CurrencyCode left, CurrencyCode right) => left.CompareTo(right) < 0;

    public static bool operator >(CurrencyCode left, CurrencyCode right) => left.CompareTo(right) > 0;

    public static bool operator <=(CurrencyCode left, CurrencyCode right) => left.CompareTo(right) <= 0;

    public static bool operator >=(CurrencyCode left, CurrencyCode right) => left.CompareTo(right) >= 0;

    public override string ToString() => Value ?? string.Empty;
}
