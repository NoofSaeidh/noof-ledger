namespace Noof.Ledger.Domain;

public sealed class Category
{
    public required Guid Id { get; init; }
    public Guid? ParentId { get; set; }

    // Immutable once minted: the model answers every categorization job with
    // this slug, so changing it would silently orphan past and future answers.
    public required string Slug { get; init; }

    public required string NameEn { get; set; }
    public required string NameRu { get; set; }
    public required bool IsActive { get; set; }
}
