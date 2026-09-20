namespace Noof.Ledger.Architecture.Tests;

public static class RepoRoot
{
    public static DirectoryInfo Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;

        return dir ?? throw new InvalidOperationException("global.json not found above the test output directory.");
    }
}
