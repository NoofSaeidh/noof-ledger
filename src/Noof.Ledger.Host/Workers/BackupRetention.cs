namespace Noof.Ledger.Host.Workers;

// Pure and stateless, nothing to substitute — same exemption as MerchantName.Fold (CLAUDE.md §3).
// Internal, not public: BackupWorker is its only caller, and CLAUDE.md §3 names the public-helper
// exemptions "not a licence to invent more by analogy."
internal static class BackupRetention
{
    public static IReadOnlyList<string> ToDelete(IReadOnlyList<string> fileNames, int keep = 14) =>
        [.. fileNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .SkipLast(Math.Min(keep, fileNames.Count))];
}
