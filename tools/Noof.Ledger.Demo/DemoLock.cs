namespace Noof.Ledger.Demo;

// The port check alone is not exclusion: two worktrees can both see 5264 free and then drop the one
// demo database under each other. An open file with no sharing is held until this process ends, even
// if it is killed, so there is no stale lock to clean up.
internal sealed class DemoLock(FileStream file) : IDisposable
{
    public static DemoLock Acquire(DemoPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        try
        {
            return new(new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            throw new InvalidOperationException(
                $"Another demo command (demo or screenshots, from any checkout) is running - wait for it to finish. Lock: {paths.LockFile}");
        }
    }

    public void Dispose() => file.Dispose();
}
