using Noof.Domain;

namespace Noof.Persistence;

public sealed class MoneyProbeEntity
{
    public int Id { get; init; }
    public required Money Amount { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}
