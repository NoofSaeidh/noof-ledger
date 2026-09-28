using Npgsql;

namespace Noof.Ledger.TestKit;

public static class DatabaseSettings
{
    // Every DROP DATABASE forces a checkpoint and waits for it. Under the default WAL_LOG strategy a
    // new database's ~300 files are written through shared buffers, so that checkpoint must fsync
    // every database created since the last one and still alive - with 40 live clones one CHECKPOINT
    // took 42.6s under WAL_LOG and 0.17s under FILE_COPY, which copies and fsyncs the files in the
    // creating backend instead. That is what made full-suite drops outlast 120s (Phase 6), with or
    // without a second checkout running; the budgets below are a ceiling, not the fix.
    //
    // This used to be one AdminDdlTimeoutSeconds backing both budgets below. They happened to share
    // a value, which let editing one look like it covered both - it did not: the connect timeout
    // and the command timeout bound entirely different waits (handshake vs. checkpoint), and a fix
    // aimed at one silently changed the other too. Two names close that trap even though the values
    // still happen to match.
    public const int AdminCommandTimeoutSeconds = 120;

    // Opening a fresh admin connection is not covered by CommandTimeout at all - that only bounds a
    // command once connected - so under the same loaded server just the TCP/protocol handshake can
    // outlast Npgsql's default 15s connect timeout, throwing through the identical "reading from
    // stream"/"timeout during reading attempt" path a slow query does. Every admin DDL connection
    // this test infrastructure opens goes through here so the connect phase gets the same grace the
    // commands below do.
    public const int AdminConnectTimeoutSeconds = 120;

    public static async Task<NpgsqlConnection> OpenAdminConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Timeout = AdminConnectTimeoutSeconds };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public static string CreateFromTemplateSql(string name) => CreateFromTemplateSql(name, TemplateDatabase);

    public static string CreateFromTemplateSql(string name, string template) =>
        $"CREATE DATABASE \"{name}\" TEMPLATE {template} STRATEGY FILE_COPY";

    public static string CreateEmptySql(string name) =>
        $"CREATE DATABASE \"{name}\" STRATEGY FILE_COPY";

    public static Task CreateDatabaseFromTemplateAsync(string name, CancellationToken cancellationToken) =>
        CreateDatabaseFromTemplateAsync(name, TemplateDatabase, cancellationToken);

    public static Task CreateDatabaseFromTemplateAsync(string name, string template, CancellationToken cancellationToken) =>
        ExecuteAdminDdlAsync(CreateFromTemplateSql(name, template), cancellationToken);

    public static Task CreateEmptyDatabaseAsync(string name, CancellationToken cancellationToken) =>
        ExecuteAdminDdlAsync(CreateEmptySql(name), cancellationToken);

    public static Task DropDatabaseAsync(string name, CancellationToken cancellationToken) =>
        ExecuteAdminDdlAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", cancellationToken);

    static async Task ExecuteAdminDdlAsync(string sql, CancellationToken cancellationToken)
    {
        await using var admin = await OpenAdminConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, admin) { CommandTimeout = AdminCommandTimeoutSeconds };
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
