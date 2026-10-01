namespace Noof.Ledger.Domain;

// The one rounding rule for a computed amount (spec §1): two decimals, a midpoint away from zero, applied once
// after the whole formula.
internal static class MoneyMath
{
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
