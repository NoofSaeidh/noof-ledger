namespace Noof.Ledger.TestKit;

public static class DatabaseSettings
{
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
