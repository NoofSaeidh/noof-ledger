namespace Noof.Ledger.Persistence.Secrets;

public sealed class AppSecret
{
    public required string Key { get; init; }
    public required string Ciphertext { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
