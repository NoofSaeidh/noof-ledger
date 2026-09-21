using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

public static class ProposalVerification
{
    const int MaxDescriptionLength = 512;
    const int MaxMerchantQuoteLength = 256;

    public static bool TryResolve(
        string rawText,
        CategorizationProposal proposal,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        out IReadOnlyList<ResolvedLineItem> items,
        out string failure)
    {
        // Assigned once, up front, and never reassigned on any failing path below - the resolved
        // list built inside the loop is only ever handed out on total success. This is what makes
        // "partial acceptance is not offered" true even when an earlier item individually verified
        // fine before a later one failed.
        items = [];

        if (proposal.Items.Count == 0)
        {
            failure = "Proposal has no items.";
            return false;
        }

        var resolved = new List<ResolvedLineItem>(proposal.Items.Count);

        for (var index = 0; index < proposal.Items.Count; index++)
        {
            var item = proposal.Items[index];
            var label = $"Item {index + 1} (\"{item.Description}\")";

            if (!QuotedAmount.TryResolve(rawText, item.AmountQuote, item.CurrencyCode, out var money, out var amountFailure))
            {
                failure = $"{label}: {amountFailure}";
                return false;
            }

            var matchedSlug = offeredSlugs.FirstOrDefault(
                slug => string.Equals(slug, item.CategorySlug, StringComparison.OrdinalIgnoreCase));
            if (matchedSlug is null)
            {
                failure = $"{label}: category slug \"{item.CategorySlug}\" was not offered.";
                return false;
            }

            if (item.KnownMerchantId is { } knownMerchantId && !offeredMerchantIds.Contains(knownMerchantId))
            {
                failure = $"{label}: known merchant id {knownMerchantId} was not offered.";
                return false;
            }

            if (!string.IsNullOrEmpty(item.MerchantQuote))
            {
                if (item.MerchantQuote.Length > MaxMerchantQuoteLength)
                {
                    failure = $"{label}: merchant quote \"{item.MerchantQuote}\" is {item.MerchantQuote.Length} characters, " +
                        $"longer than the {MaxMerchantQuoteLength}-character merchant name column.";
                    return false;
                }

                if (!rawText.Contains(item.MerchantQuote, StringComparison.Ordinal))
                {
                    failure = $"{label}: merchant quote \"{item.MerchantQuote}\" does not occur verbatim in the raw text.";
                    return false;
                }
            }

            var description = item.Description.Length > MaxDescriptionLength
                ? item.Description[..MaxDescriptionLength]
                : item.Description;

            resolved.Add(new ResolvedLineItem(description, money, matchedSlug, item.KnownMerchantId, item.MerchantQuote));
        }

        items = resolved;
        failure = string.Empty;
        return true;
    }
}
