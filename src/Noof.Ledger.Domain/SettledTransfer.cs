namespace Noof.Ledger.Domain;

// What each wallet actually moved (T-12): From is everything that left the source, To everything that reached the
// destination, the fee inside the amount of its leg.
public sealed record SettledTransfer(Money From, Money To, Money? Fee, TransferLeg? FeeLeg);
