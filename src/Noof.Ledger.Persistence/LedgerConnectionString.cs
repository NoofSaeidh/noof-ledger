using Npgsql;

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
            return WithoutErrorDetail(fromConfiguration);

        // NOOF_TEST_PG is deliberately NOT consulted here. It is the test suites' variable, set
        // machine-wide by ops/reset-database-auth.ps1 and pointing at the postgres database - and
        // this method would rewrite it to name noof_ledger, the operator's real ledger. That turned
        // a stray launch of the published host into a silent attachment to real financial data,
        // which happened once during Phase 1B. Tests read the variable through
        // Noof.Ledger.TestKit.DatabaseSettings instead, which is the only thing that should.
        if (File.Exists(CredentialFile))
        {
            var fromFile = File.ReadAllText(CredentialFile).Trim();
            if (!string.IsNullOrWhiteSpace(fromFile))
                return WithoutErrorDetail(ForDatabase(fromFile, database));
        }

        throw new InvalidOperationException(
            $"No PostgreSQL connection string. Set ConnectionStrings:Ledger, or run " +
            $"ops/reset-database-auth.ps1 to create {CredentialFile}.");
    }

    // Npgsql writes parameter VALUES into exception messages when this is on. The ops script puts it
    // in the credential file, which is useful against throwaway test databases and unacceptable on the
    // application path, where those parameters are ciphertext and a person's spending.
    static string WithoutErrorDetail(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.IncludeErrorDetail)
            return connectionString;

        builder.IncludeErrorDetail = false;
        builder.Remove("Include Error Detail");
        return builder.ConnectionString;
    }

    static string ForDatabase(string connectionString, string database) =>
        connectionString.Replace("Database=postgres", $"Database={database}", StringComparison.Ordinal);
}
