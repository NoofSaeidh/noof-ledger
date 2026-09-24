namespace Noof.Ledger.Domain;

public sealed class Wallet
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public required CurrencyCode Currency { get; init; }
    public string[] Aliases { get; set; } = [];

    // At most one per currency. A partial unique index holds that, not this class.
    public bool IsDefaultForCurrency { get; set; }

    public bool Archived { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
