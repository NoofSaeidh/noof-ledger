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
