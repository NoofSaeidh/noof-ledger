using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

public static class MerchantScan
{
    public static IReadOnlyList<MerchantAliasEntry> Matches(
        string rawText, IReadOnlyList<MerchantAliasEntry> aliases, int limit)
    {
        if (limit <= 0)
            return [];

        var rawTokens = MerchantName.Fold(rawText).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rawTokens.Length == 0)
            return [];

        return aliases
            .Where(alias => OccursAsTokenSequence(rawTokens, alias.Folded.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .OrderByDescending(alias => alias.Folded.Length)
            .ThenBy(alias => alias.Folded, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    // Token-array equality, not a substring search: matching "MAXI" with IndexOf/Contains would
    // also match inside "MAXIMALNO", and a culture-aware IndexOf risks the Serbian LJ-collation
    // defect this codebase treats as settled everywhere else. Folded text is space-separated by
    // construction (MerchantName.Fold), which is what makes comparing token arrays directly work.
    static bool OccursAsTokenSequence(string[] rawTokens, string[] aliasTokens)
    {
        if (aliasTokens.Length == 0 || aliasTokens.Length > rawTokens.Length)
            return false;

        for (var start = 0; start <= rawTokens.Length - aliasTokens.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < aliasTokens.Length; offset++)
            {
                if (!string.Equals(rawTokens[start + offset], aliasTokens[offset], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return true;
        }

        return false;
    }
}
