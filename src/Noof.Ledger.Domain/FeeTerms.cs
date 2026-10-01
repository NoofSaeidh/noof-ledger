namespace Noof.Ledger.Domain;

// A wallet's fee on a foreign-currency purchase (T-6): a percentage, a fixed amount, a minimum, or any
// combination; a missing term counts as zero.
public sealed record FeeTerms(decimal? Percent, decimal? Fixed, decimal? Minimum)
{
    public static readonly FeeTerms None = new(null, null, null);

    public decimal FeeOn(decimal charged) =>
        MoneyMath.Round(Math.Max(charged * (Percent ?? 0m) / 100m + (Fixed ?? 0m), Minimum ?? 0m));
}
