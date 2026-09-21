using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Persistence.Categorization;

public sealed class EfCategoryCatalog(LedgerDbContext db) : ICategoryCatalog
{
    public async Task<IReadOnlyList<CategoryEntry>> ActiveAsync(CancellationToken cancellationToken) =>
        await (
            from c in db.Categories.AsNoTracking()
            where c.IsActive
            join p in db.Categories.AsNoTracking() on c.ParentId equals p.Id into parents
            from p in parents.DefaultIfEmpty()
            select new CategoryEntry(c.Id, c.Slug, c.NameEn, c.NameRu, p == null ? null : p.Slug))
            .ToListAsync(cancellationToken);
}
