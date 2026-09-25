namespace Noof.Ledger.Host.Diagnostics;

internal interface IFreeSpaceProvider
{
    long GetAvailableFreeBytes(string path);
}

internal sealed class DriveFreeSpaceProvider : IFreeSpaceProvider
{
    public long GetAvailableFreeBytes(string path)
    {
        Directory.CreateDirectory(path);
        return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
    }
}
