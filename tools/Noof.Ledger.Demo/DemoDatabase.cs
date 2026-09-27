using Npgsql;

namespace Noof.Ledger.Demo;

internal static class DemoDatabase
{
    public const string Name = "noof_ledger_demo";
    public const int Port = 5264;
    const string TestDatabasePrefix = "noof_test_";

    public static string DefaultCredentialFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "db.connection");

    public static string AdminConnectionString(string credentialFile) =>
        File.Exists(credentialFile)
            ? File.ReadAllText(credentialFile).Trim()
            : throw new InvalidOperationException(
                $"No PostgreSQL credential at {credentialFile}. Run .\\run.ps1 db-auth-reset once to create it.");

    public static string For(string adminConnectionString, string database)
    {
        EnsureDisposable(database);
        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = database }.ConnectionString;

        return new NpgsqlConnectionStringBuilder(connectionString).Database == database
            ? connectionString
            : throw new InvalidOperationException($"Could not point the connection string at {database}.");
    }

    public static void EnsureDisposable(string database)
    {
        if (database != Name && !database.StartsWith(TestDatabasePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to touch database '{database}': the demo tool only rebuilds {Name} or a {TestDatabasePrefix}* database.");
    }

    public static async Task RecreateAsync(string adminConnectionString, string database, CancellationToken cancellationToken)
    {
        EnsureDisposable(database);
        NpgsqlConnection.ClearAllPools();

        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", cancellationToken);
            await ExecuteAsync(admin, $"CREATE DATABASE \"{database}\" STRATEGY FILE_COPY", cancellationToken);
        }

        await using var target = new NpgsqlConnection(For(adminConnectionString, database));
        await target.OpenAsync(cancellationToken);
        await ExecuteAsync(target, "CREATE EXTENSION IF NOT EXISTS pg_trgm; CREATE EXTENSION IF NOT EXISTS unaccent;", cancellationToken);
    }

    // DROP DATABASE waits for a checkpoint, which must fsync every database created under the default
    // WAL_LOG strategy since the last one - FILE_COPY keeps this one out of it, as TestKit's
    // DatabaseSettings does for the test clones. The timeout matches its admin command budget.
    static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
