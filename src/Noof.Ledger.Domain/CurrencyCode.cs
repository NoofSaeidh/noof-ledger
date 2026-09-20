namespace Noof.Ledger.Domain;

public readonly record struct CurrencyCode
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

    public override string ToString() => Value;
}
