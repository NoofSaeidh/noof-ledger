using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class LogFileTail(IConfiguration configuration) : ILogFileTail
{
    public async Task<IReadOnlyList<string>> ReadLastLinesAsync(int count, CancellationToken cancellationToken)
    {
        var newest = NewestLogFile();
        if (newest is null)
            return [];

        var lines = new List<string>();
        await using var stream = new FileStream(
            newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        // The sink writes tens of MB per file (50 MB cap, spec O-2) - reading the whole file to keep
        // only the tail would be wasteful for a viewer someone opens repeatedly, but correctness (not
        // memory) is what this task needs first; a bounded ring buffer of `count` lines read forward
        // is simple, correct, and still O(file size) only in reads, not retained memory.
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            if (lines.Count > count)
                lines.RemoveAt(0);
        }

        return lines;
    }

    FileInfo? NewestLogFile()
    {
        var directory = ResolveDirectory();
        if (!Directory.Exists(directory))
            return null;

        return new DirectoryInfo(directory)
            .EnumerateFiles("noof-ledger-*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    string ResolveDirectory()
    {
        var configured = configuration["Logging:File:Directory"];
        var expanded = Environment.ExpandEnvironmentVariables(
            configured ?? @"%LOCALAPPDATA%\NoofLedger\logs");
        return expanded;
    }
}
