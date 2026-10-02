using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Revisions;

internal sealed record ParsedCharge(
    CurrencyCode Currency, decimal ChargedAmount, decimal FeeAmount, decimal RateUsed, decimal? FeePercent, decimal? FeeFixed,
    decimal? FeeMinimum, ChargeSource Source);
