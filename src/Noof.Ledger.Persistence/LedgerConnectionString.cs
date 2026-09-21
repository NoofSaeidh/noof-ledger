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

        var fromEnvironment = Environment.GetEnvironmentVariable("NOOF_TEST_PG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return WithoutErrorDetail(ForDatabase(fromEnvironment, database));

        if (File.Exists(CredentialFile))
        {
            var fromFile = File.ReadAllText(CredentialFile).Trim();
            if (!string.IsNullOrWhiteSpace(fromFile))
                return WithoutErrorDetail(ForDatabase(fromFile, database));
        }

        throw new InvalidOperationException(
            $"No PostgreSQL connection string. Set ConnectionStrings:Ledger, or NOOF_TEST_PG, or run " +
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
