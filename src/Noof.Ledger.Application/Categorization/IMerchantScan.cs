namespace Noof.Ledger.Application.Categorization;

public interface IMerchantScan
{
    IReadOnlyList<MerchantAliasEntry> Matches(string rawText, IReadOnlyList<MerchantAliasEntry> aliases, int limit);
}
