using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Noof.Ledger.Persistence;

// Nothing calls this and nothing should: `dotnet ef` finds it by reflection at design time, which
// is how migrations are scaffolded at all. Verified still working after LedgerDbContext went
// internal - `dotnet ef dbcontext info` reports the same context and provider as before.
// ReSharper disable once UnusedType.Global
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    // run.ps1's update-test-template hands the (password-bearing) test-template connection string
    // through this environment variable instead of `dotnet ef database update --connection`, so the
    // password never appears as an argument on the `dotnet ef` child process's command line - the
    // same rule CLAUDE.md's Database section holds pg_dump to. Never consulted for anything but this.
    internal const string ConnectionOverrideVariable = "NOOF_LEDGER_EF_CONNECTION";

    public LedgerDbContext CreateDbContext(string[] args)
    {
        var overrideConnection = Environment.GetEnvironmentVariable(ConnectionOverrideVariable);
        var connectionString = string.IsNullOrWhiteSpace(overrideConnection)
            ? LedgerConnectionString.Resolve(null)
            : overrideConnection;

        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new LedgerDbContext(options);
    }
}
