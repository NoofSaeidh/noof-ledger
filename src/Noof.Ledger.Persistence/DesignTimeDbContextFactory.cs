using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Noof.Ledger.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NoofDbContext>
{
    public NoofDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NoofDbContext>()
            .UseNpgsql(ResolveConnectionString())
            .Options;

        return new NoofDbContext(options);
    }

    private static string ResolveConnectionString()
    {
        var credentialFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoofFinance",
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
        connectionString.Replace("Database=postgres", "Database=noof_finance", StringComparison.Ordinal);
}
