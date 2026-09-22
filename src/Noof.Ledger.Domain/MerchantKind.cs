namespace Noof.Ledger.Domain;

// Service and ExchangeVenue are unreferenced in code today and must stay: these values are
// persisted in the merchants table, so the enum is a storage vocabulary rather than a set of
// call sites. ExchangeVenue is what Phase 5's realised-rate work reads. Deleting an unused member
// here would renumber nothing but would silently make an existing row unreadable.
// ReSharper disable UnusedMember.Global
public enum MerchantKind
{
    Retail = 0,
    Service = 1,
    ExchangeVenue = 2,
}
