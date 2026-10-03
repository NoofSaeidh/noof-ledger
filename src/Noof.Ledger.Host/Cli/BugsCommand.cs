using System.Globalization;
using System.Text;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.Host.Cli;

internal sealed record BugsExportArguments(bool All, string OutputDirectory);

internal static class BugsCommand
{
    public const string Usage = "Usage: bugs export [--all] [--output <directory>]";
    public const string DefaultOutputDirectory = "artifacts/bug-reports";

    public static bool TryParse(string[] args, out BugsExportArguments? arguments)
    {
        arguments = args is ["bugs", "export", .. var options] ? ParseExportOptions(options) : null;
        return args is ["bugs", ..];
    }

    static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static async Task<int> ExportAsync(
        IServiceProvider services, BugsExportArguments arguments, TimeProvider timeProvider, TextWriter output,
        CancellationToken cancellationToken)
    {
        try
        {
            await services.OpenNoofDatabaseConnectionAsync(cancellationToken);
        }
        catch (Exception unreachable) when (unreachable is not OperationCanceledException)
        {
            await output.WriteLineAsync($"Cannot reach PostgreSQL ({unreachable.GetType().Name}) - start it and try again.");
            return 1;
        }

        try
        {
            var pending = await services.CountPendingNoofMigrationsAsync(cancellationToken);
            if (pending > 0)
            {
                await output.WriteLineAsync(
                    $"The database is not migrated ({pending} pending) - start the app once to migrate it.");
                return 1;
            }

            await using var scope = services.CreateAsyncScope();
            var reports = await scope.ServiceProvider.GetRequiredService<IBugReportStore>()
                .LoadForExportAsync(arguments.All, cancellationToken);
            if (reports.Count == 0)
            {
                await output.WriteLineAsync(arguments.All ? "No bug reports." : "No open bug reports.");
                return 0;
            }

            var markdown = scope.ServiceProvider.GetRequiredService<IBugReportMarkdown>()
                .Render(reports, timeProvider.GetUtcNow());

            Directory.CreateDirectory(arguments.OutputDirectory);
            var path = Path.Combine(
                arguments.OutputDirectory,
                string.Create(CultureInfo.InvariantCulture, $"{timeProvider.GetLocalNow():yyyy-MM-dd-HHmm}.md"));
            await File.WriteAllTextAsync(path, markdown, Utf8WithoutBom, cancellationToken);

            await output.WriteLineAsync(path);
            return 0;
        }
        catch (Exception failed) when (failed is not OperationCanceledException)
        {
            await output.WriteLineAsync($"Export failed ({failed.GetType().Name}).");
            return 1;
        }
    }

    static BugsExportArguments? ParseExportOptions(string[] options)
    {
        var all = false;
        string? output = null;

        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--all" when !all:
                    all = true;
                    break;
                case "--output" when output is null && i + 1 < options.Length && IsDirectoryValue(options[i + 1]):
                    i++;
                    output = options[i];
                    break;
                default:
                    return null;
            }
        }

        return new BugsExportArguments(all, Path.GetFullPath(output ?? DefaultOutputDirectory));
    }

    static bool IsDirectoryValue(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith("--", StringComparison.Ordinal);
}
