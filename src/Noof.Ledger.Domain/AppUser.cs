namespace Noof.Ledger.Domain;

public sealed class AppUser
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }
    public required string PasswordHash { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
}
