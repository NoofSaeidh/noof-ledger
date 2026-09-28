namespace Noof.Ledger.Demo;

internal sealed record DemoPaths(string Root)
{
    public string Logs => Path.Combine(Root, "logs");
    public string KeyRing => Path.Combine(Root, "dp-keys");
    public string LockFile => Path.Combine(Root, "demo.lock");

    public static DemoPaths ForOperator() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "demo"));
}
