namespace Noof.Ledger.Persistence;

public static class LedgerConnectionString
{
    public const string DefaultDatabase = "noof_ledger";

    public static string CredentialFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoofLedger",
        "db.connection");

    public static string Resolve(string? fromConfiguration, string database = DefaultDatabase)
    {
        if (!string.IsNullOrWhiteSpace(fromConfiguration))
            return fromConfiguration;

        var fromEnvironment = Environment.GetEnvironmentVariable("NOOF_TEST_PG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return ForDatabase(fromEnvironment, database);

        if (File.Exists(CredentialFile))
        {
            var fromFile = File.ReadAllText(CredentialFile).Trim();
            if (!string.IsNullOrWhiteSpace(fromFile))
                return ForDatabase(fromFile, database);
        }

        throw new InvalidOperationException(
            $"No PostgreSQL connection string. Set ConnectionStrings:Ledger, or NOOF_TEST_PG, or run " +
            $"ops/reset-database-auth.ps1 to create {CredentialFile}.");
    }

    static string ForDatabase(string connectionString, string database) =>
        connectionString.Replace("Database=postgres", $"Database={database}", StringComparison.Ordinal);
}
