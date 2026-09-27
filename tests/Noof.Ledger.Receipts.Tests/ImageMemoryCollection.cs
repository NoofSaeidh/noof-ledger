namespace Noof.Ledger.Receipts.Tests;

// The oversized-header tests measure the process working set, which any image test running
// alongside them would inflate - so the classes that measure run alone.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ImageMemoryCollection
{
    public const string Name = "Image memory";
}
