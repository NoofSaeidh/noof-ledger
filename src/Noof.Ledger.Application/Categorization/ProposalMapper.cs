using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

// Maps, never judges. Amount is already a decimal by the time it reaches here — the model answered a
// JSON number and System.Text.Json read it straight in, so there is no amount parsing left to do.
// Whether a figure is plausible, or appears in the message at all, is for the person to see in the
// echo and correct there (D1, docs/OPEN-QUESTIONS.md P2-1). Do not add a sanity bound or a verbatim
// check here.
public static class ProposalMapper
{
    const int MaxDescriptionLength = 512;
    const int MaxMerchantNameLength = 256;

    public static bool TryMap(
        CategorizationProposal proposal,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        string defaultCurrency,
        out MappedProposal mapped,
        out string failure)
    {
        mapped = new MappedProposal([]);
        var items = new List<ResolvedLineItem>(proposal.Items.Count);

        for (var index = 0; index < proposal.Items.Count; index++)
        {
            var item = proposal.Items[index];
            if (MapItem(item, offeredSlugs, offeredMerchantIds, defaultCurrency, out var reason) is not { } resolved)
            {
                failure = $"Item {index + 1} (\"{item.Description}\"): {reason}";
                return false;
            }

            items.Add(resolved);
        }

        mapped = new MappedProposal(items);
        failure = string.Empty;
        return true;
    }

    static ResolvedLineItem? MapItem(
        ProposedLineItem item,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        string defaultCurrency,
        out string reason)
    {
        var code = string.IsNullOrWhiteSpace(item.CurrencyCode) ? defaultCurrency : item.CurrencyCode.Trim();
        var currency = CurrencyCode.Supported.FirstOrDefault(
            supported => string.Equals(supported.Value, code, StringComparison.OrdinalIgnoreCase));
        if (currency.Value is null)
        {
            reason = $"currency \"{code}\" is not one this ledger supports.";
            return null;
        }

        var slug = offeredSlugs.FirstOrDefault(
            offered => string.Equals(offered, item.CategorySlug, StringComparison.OrdinalIgnoreCase));
        if (slug is null)
        {
            reason = $"category slug \"{item.CategorySlug}\" was not offered.";
            return null;
        }

        if (item.KnownMerchantId is { } merchantId && !offeredMerchantIds.Contains(merchantId))
        {
            reason = $"known merchant id {merchantId} was not offered.";
            return null;
        }

        reason = string.Empty;
        return new ResolvedLineItem(
            Truncate(item.Description, MaxDescriptionLength),
            new Money(item.Amount, currency),
            slug,
            item.KnownMerchantId,
            string.IsNullOrWhiteSpace(item.MerchantName) ? null : Truncate(item.MerchantName.Trim(), MaxMerchantNameLength));
    }

    static string Truncate(string text, int maxLength) => text.Length > maxLength ? text[..maxLength] : text;
}
