using System.Globalization;

namespace Noof.Ledger.Domain;

// "1 Base = QuoteAmount Quote", in whichever direction the operator or the slip wrote it.
public readonly record struct ExchangeRate(CurrencyCode Base, decimal QuoteAmount, CurrencyCode Quote)
{
    public Money Convert(Money amount)
    {
        RequirePositive();

        if (amount.Currency == Base)
            return new Money(amount.Amount * QuoteAmount, Quote).Round();

        if (amount.Currency == Quote)
            return new Money(amount.Amount / QuoteAmount, Base).Round();

        throw new CurrencyMismatchException(amount.Currency, Base);
    }

    // The smaller amount is the dearer unit, so the rate reads "1 dearer = N cheaper" from the start.
    public static ExchangeRate Between(Money first, Money second)
    {
        if (first.Currency == second.Currency)
            throw new ArgumentException($"A rate needs two currencies; both amounts are {first.Currency}.", nameof(second));

        if (first.Amount <= 0 || second.Amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(first), "A realised rate needs two positive amounts.");

        var (dearer, cheaper) = first.Amount <= second.Amount ? (first, second) : (second, first);
        return new ExchangeRate(dearer.Currency, cheaper.Amount / dearer.Amount, cheaper.Currency);
    }

    public ExchangeRate Oriented()
    {
        RequirePositive();

        return QuoteAmount < 1 ? new ExchangeRate(Quote, 1 / QuoteAmount, Base) : this;
    }

    public override string ToString()
    {
        var oriented = Oriented();
        return $"1 {oriented.Base} = {oriented.QuoteAmount.ToString("0.0000", CultureInfo.InvariantCulture)} {oriented.Quote}";
    }

    void RequirePositive()
    {
        if (QuoteAmount <= 0)
            throw new InvalidOperationException($"A rate of {QuoteAmount} {Quote} per {Base} cannot convert anything.");
    }
}
