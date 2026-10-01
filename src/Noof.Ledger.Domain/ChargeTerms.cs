namespace Noof.Ledger.Domain;

public sealed record ComputedCharge(decimal Charged, decimal Fee, decimal RateUsed);

// What a purchase in a foreign currency costs a wallet: its rate (wallet-currency units per one foreign unit) and
// its fee terms. A charge that is not positive is no charge - that currency stays an unconverted line (M10).
public sealed record ChargeTerms(decimal Rate, FeeTerms Fee)
{
    public ComputedCharge? ChargeFor(decimal foreignSum)
    {
        var charged = MoneyMath.Round(foreignSum * Rate);

        return charged > 0 ? new ComputedCharge(charged, Fee.FeeOn(charged), Rate) : null;
    }

    // Static: a wallet with no terms for the currency has no Rate, yet what the operator said was charged still
    // stands (the caller passes FeeTerms.None then).
    public static ComputedCharge? Stated(decimal stated, decimal foreignSum, decimal? statedFee, bool feeIncluded, FeeTerms fee)
    {
        // A negative fee is model noise, not a refund: it would only reach ck_charges_amounts as a failed write.
        if (foreignSum <= 0 || statedFee < 0)
            return null;

        var (charged, chargedFee) = (feeIncluded, statedFee) switch
        {
            (true, { } said) => (MoneyMath.Round(stated - said), MoneyMath.Round(said)),
            (true, null) => FeeInside(stated, fee),
            (false, { } said) => (MoneyMath.Round(stated), MoneyMath.Round(said)),
            (false, null) => (MoneyMath.Round(stated), fee.FeeOn(MoneyMath.Round(stated))),
        };

        return charged > 0
            ? new ComputedCharge(charged, chargedFee, Math.Round(charged / foreignSum, 12, MidpointRounding.AwayFromZero))
            : null;
    }

    // A fee said to be inside the figure but not said: over the reals, f = max(p·(stated − f) + fixed, minimum) has
    // exactly one solution, max((p·stated + fixed) / (1 + p), minimum). In cents there may be none, so the fee is
    // that solution rounded and can differ from FeeOn(Charged) by a cent; Charged + Fee == stated always holds.
    static (decimal Charged, decimal Fee) FeeInside(decimal stated, FeeTerms terms)
    {
        var percent = (terms.Percent ?? 0m) / 100m;
        var fee = MoneyMath.Round(Math.Max((stated * percent + (terms.Fixed ?? 0m)) / (1m + percent), terms.Minimum ?? 0m));

        return (MoneyMath.Round(stated - fee), fee);
    }
}
