namespace Noof.Ledger.Host.Diagnostics;

internal static class DatabasePassword
{
    public static string From(string connectionString) =>
        new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Password ?? string.Empty;
}
