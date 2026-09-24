using System.Text.RegularExpressions;

namespace Noof.Ledger.Host.Workers;

// Pure and stateless, nothing to substitute — same exemption as MerchantName.Fold (CLAUDE.md §3).
// Internal, not public: BackupWorker is its only caller, and CLAUDE.md §3 names the public-helper
// exemptions "not a licence to invent more by analogy."
internal static partial class BackupRetention
{
    // M-3 (Phase 4 final review): "newest by name" trusted the glob that listed these files in the
    // first place. A hand-named file such as "noof_ledger-manual.dump" ('m' > '2', ordinally) would
    // sort as newest forever, so it must never even be considered here - not just excluded from
    // deletion, since being "kept" is exactly what let it stand in for a real dump.
    [GeneratedRegex(@"^noof_ledger-\d{8}-\d{6}\.dump$")]
    private static partial Regex AutomatedDumpName();

    public static IReadOnlyList<string> ToDelete(IReadOnlyList<string> fileNames, int keep = 14)
    {
        var dumps = fileNames.Where(name => AutomatedDumpName().IsMatch(name)).ToArray();
        return [.. dumps
            .OrderBy(name => name, StringComparer.Ordinal)
            .SkipLast(Math.Min(keep, dumps.Length))];
    }
}
