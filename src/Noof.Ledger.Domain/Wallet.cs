namespace Noof.Ledger.Domain;

public sealed class Wallet
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public required CurrencyCode Currency { get; init; }

    // Exactly one wallet must have this set to true; ICaptureStore.CaptureAsync
    // (a later task) resolves the default wallet by querying for it. Nothing in
    // this class enforces "exactly one" - that invariant lives with the caller.
    public required bool IsDefault { get; set; }
}
