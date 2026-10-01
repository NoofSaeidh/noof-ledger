namespace Noof.Ledger.Domain;

// A fee as the operator said it: on which leg, and whether the amount said for that leg already includes it.
public sealed record TransferFeeRequest(Money Amount, TransferLeg Leg, bool Included);
