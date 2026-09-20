namespace Noof.Ledger.E2E.Tests;

static class RepoRoot
{
    public static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            directory = directory.Parent;

        if (directory is null)
            throw new InvalidOperationException("Could not locate the repository root from the test output directory.");

        return directory.FullName;
    }
}
