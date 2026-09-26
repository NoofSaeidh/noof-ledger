namespace Noof.Ledger.Host.Diagnostics;

internal interface IFreeSpaceProvider
{
    long GetAvailableFreeBytes(string path);
}

internal sealed class DriveFreeSpaceProvider : IFreeSpaceProvider
{
    // M-5 (Phase 5 final review): a read-only health check must not have the side effect of
    // creating directories. DriveInfo measures free space at the volume level, so the path itself
    // never needs to exist - Path.GetPathRoot(Path.GetFullPath(path)) resolves the drive root alone.
    public long GetAvailableFreeBytes(string path) =>
        new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
}
