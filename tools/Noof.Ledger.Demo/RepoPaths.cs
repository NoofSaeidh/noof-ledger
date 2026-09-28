namespace Noof.Ledger.Demo;

internal static class RepoPaths
{
    public static DirectoryInfo Root { get; } = FindRoot();
    public static string Host => Path.Combine(Root.FullName, "src", "Noof.Ledger.Host");
    public static string Screenshots => Path.Combine(Root.FullName, "docs", "screenshots");
    public static string SendReady => Path.Combine(Root.FullName, "artifacts", "screenshots");

    static DirectoryInfo FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            directory = directory.Parent;

        return directory ?? throw new InvalidOperationException("global.json not found above the demo tool's output directory.");
    }
}
