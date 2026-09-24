using System.Diagnostics;
using System.Globalization;
using Noof.Ledger.Application.Backup;
using Npgsql;

namespace Noof.Ledger.Persistence.Backup;

// A pure function so "the password never reaches an argument" is a unit test against a plain
// string list, not something only provable by launching a real process (CLAUDE.md §4 "External
// processes": ProcessStartInfo.Environment carries the password, ArgumentList never does).
internal static class PgDumpArguments
{
    public static IReadOnlyList<string> Build(NpgsqlConnectionStringBuilder connection, string targetPath) =>
    [
        "-Fc",
        "-h", connection.Host ?? "localhost",
        "-p", connection.Port.ToString(CultureInfo.InvariantCulture),
        "-U", connection.Username ?? string.Empty,
        "-d", connection.Database ?? string.Empty,
        "-f", targetPath,
    ];
}

internal sealed class PgDumpDatabaseDumper(string connectionString, string pgDumpPath) : IDatabaseDumper
{
    public const string DefaultPath = @"C:\Program Files\PostgreSQL\18\bin\pg_dump.exe";

    public async Task<DumpResult> DumpAsync(string targetPath, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);

        var start = new ProcessStartInfo
        {
            FileName = pgDumpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in PgDumpArguments.Build(connection, targetPath))
            start.ArgumentList.Add(argument);

        // The one and only place the password reaches the child process (CLAUDE.md §4).
        start.Environment["PGPASSWORD"] = connection.Password ?? string.Empty;

        using var process = new Process { StartInfo = start };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = Scrub(await stderrTask, connection.Password);
        await stdoutTask;

        return process.ExitCode == 0
            ? new DumpResult(true, null)
            : new DumpResult(false, string.IsNullOrWhiteSpace(stderr) ? $"pg_dump exited with code {process.ExitCode}" : stderr.Trim());
    }

    // Defence in depth, not the primary guarantee: pg_dump itself never echoes a working
    // password, but a wrong one can appear in a connection-refused message from some drivers,
    // and CLAUDE.md §4 says the password never reaches a recorded error, full stop.
    static string Scrub(string text, string? password) =>
        string.IsNullOrEmpty(password) ? text : text.Replace(password, "***", StringComparison.Ordinal);
}
