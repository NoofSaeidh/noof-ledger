namespace Noof.Ledger.Application.Categorization;

public interface ICategoryCatalog
{
    Task<IReadOnlyList<CategoryEntry>> ActiveAsync(CancellationToken cancellationToken);
}
