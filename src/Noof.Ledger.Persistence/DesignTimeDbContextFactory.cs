using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Noof.Ledger.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(ResolveConnectionString())
            .Options;

        return new LedgerDbContext(options);
    }

    private static string ResolveConnectionString()
    {
        var credentialFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoofLedger",
            "db.connection");

        var fromEnvironment = Environment.GetEnvironmentVariable("NOOF_TEST_PG");
        if (!string.IsNullOrEmpty(fromEnvironment))
            return ForDesignTimeDatabase(fromEnvironment);

        if (File.Exists(credentialFile))
        {
            var fromFile = File.ReadAllText(credentialFile).Trim();
            if (!string.IsNullOrEmpty(fromFile))
                return ForDesignTimeDatabase(fromFile);
        }

        throw new InvalidOperationException(
            $"No PostgreSQL connection string found. Set the NOOF_TEST_PG environment variable or " +
            $"create {credentialFile}. Run ops/reset-database-auth.ps1 to generate it.");
    }

    private static string ForDesignTimeDatabase(string connectionString) =>
        connectionString.Replace("Database=postgres", "Database=noof_ledger", StringComparison.Ordinal);
}
