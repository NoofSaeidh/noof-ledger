using Npgsql;

namespace Noof.Ledger.TestKit;

public static class DatabaseSettings
{
    // CREATE DATABASE ... TEMPLATE ... and DROP DATABASE ... WITH (FORCE) both force PostgreSQL to
    // checkpoint the database they read from or remove - documented behaviour, not a defect - and
    // under a loaded shared server (several worktrees, or several test collections, each creating
    // and dropping clone databases) that wait can outlast Npgsql's default 30s CommandTimeout.
    // Several fixtures had already raised it for DROP alone; CREATE never got the same treatment,
    // which is the gap a Phase 6 full-suite run's flaky Npgsql read timeouts traced back to.
    public const int AdminDdlTimeoutSeconds = 120;

    // Opening a fresh admin connection is not covered by CommandTimeout at all - that only bounds a
    // command once connected - so under the same loaded server just the TCP/protocol handshake can
    // outlast Npgsql's default 15s connect timeout, throwing through the identical "reading from
    // stream"/"timeout during reading attempt" path a slow query does. Every admin DDL connection
    // this test infrastructure opens goes through here so the connect phase gets the same grace the
    // commands below do.
    public static async Task<NpgsqlConnection> OpenAdminConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Timeout = AdminDdlTimeoutSeconds };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public static Task CreateDatabaseFromTemplateAsync(string name, CancellationToken cancellationToken) =>
        ExecuteAdminDdlAsync($"CREATE DATABASE \"{name}\" TEMPLATE {TemplateDatabase}", cancellationToken);

    public static Task CreateEmptyDatabaseAsync(string name, CancellationToken cancellationToken) =>
        ExecuteAdminDdlAsync($"CREATE DATABASE \"{name}\"", cancellationToken);

    public static Task DropDatabaseAsync(string name, CancellationToken cancellationToken) =>
        ExecuteAdminDdlAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", cancellationToken);

    static async Task ExecuteAdminDdlAsync(string sql, CancellationToken cancellationToken)
    {
        await using var admin = await OpenAdminConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, admin) { CommandTimeout = AdminDdlTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Parallel worktrees each add migrations to the template independently, so each needs its own clone.
    public static string TemplateDatabase =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOOF_TEST_TEMPLATE"))
            ? "noof_ledger_test_template"
            : Environment.GetEnvironmentVariable("NOOF_TEST_TEMPLATE")!;

    private static readonly string CredentialFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoofLedger",
        "db.connection");

    public static string AdminConnectionString
    {
        get
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("NOOF_TEST_PG");
            if (!string.IsNullOrEmpty(fromEnvironment))
                return fromEnvironment;

            if (File.Exists(CredentialFile))
            {
                var fromFile = File.ReadAllText(CredentialFile).Trim();
                if (!string.IsNullOrEmpty(fromFile))
                    return fromFile;
            }

            throw new InvalidOperationException(
                $"No PostgreSQL connection string found. Set the NOOF_TEST_PG environment variable or " +
                $"create {CredentialFile}. Run ops/reset-database-auth.ps1 to generate it.");
        }
    }

    public static string For(string database) =>
        AdminConnectionString.Replace("Database=postgres", $"Database={database}", StringComparison.Ordinal);
}
