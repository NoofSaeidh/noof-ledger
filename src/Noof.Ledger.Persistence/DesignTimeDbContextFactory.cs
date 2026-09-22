using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Noof.Ledger.Persistence;

// Nothing calls this and nothing should: `dotnet ef` finds it by reflection at design time, which
// is how migrations are scaffolded at all. Verified still working after LedgerDbContext went
// internal - `dotnet ef dbcontext info` reports the same context and provider as before.
// ReSharper disable once UnusedType.Global
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(LedgerConnectionString.Resolve(null))
            .Options;

        return new LedgerDbContext(options);
    }
}
