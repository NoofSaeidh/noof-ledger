using System.Globalization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

// Maps, never judges. Amount is already a decimal by the time it reaches here — the model answered a
// JSON number and System.Text.Json read it straight in, so there is no amount parsing left to do.
// Whether a figure is plausible, or appears in the message at all, is for the person to see in the
// echo and correct there (D1, docs/OPEN-QUESTIONS.md P2-1). Do not add a sanity bound or a verbatim
// check here. The same holds for the wallet and the kind: an offered wallet id and a known kind map,
// and whether income "looks like" income is not this class's question (M3, M9).
internal sealed class ProposalMapper : IProposalMapper
{
    const int MaxDescriptionLength = 512;
    const int MaxMerchantNameLength = 256;

    public bool TryMap(
        CategorizationProposal proposal,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        IReadOnlyList<WalletOption> wallets,
        string defaultCurrency,
        out MappedProposal mapped,
        out string failure)
    {
        mapped = new MappedProposal([], null);

        if (KindOf(proposal.Kind) is not { } kind)
        {
            failure = $"kind \"{proposal.Kind}\" is not expense, income or balance.";
            return false;
        }

        if (WalletFor(proposal, kind, wallets, defaultCurrency, out failure) is not { } wallet)
            return false;

        IReadOnlyList<ResolvedLineItem> items = [];
        Money? stated = null;

        if (kind == TransactionKind.BalanceCheck)
        {
            stated = StatementOf(proposal, wallet, out failure);
            if (stated is null)
                return false;
        }
        else
        {
            var resolved = MapItems(proposal.Items, offeredSlugs, offeredMerchantIds, wallet.Currency.Value, out failure);
            if (resolved is null)
                return false;

            items = resolved;
        }

        if (!TryParseDay(proposal.OccurredOn, out var occurredOn, out failure))
            return false;

        mapped = new MappedProposal(items, occurredOn, kind, wallet.Id, stated);
        failure = string.Empty;
        return true;
    }

    static TransactionKind? KindOf(string kind) => kind switch
    {
        ProposedKind.Expense => TransactionKind.Expense,
        ProposedKind.Income => TransactionKind.Income,
        ProposedKind.Balance => TransactionKind.BalanceCheck,
        _ => null,
    };

    static WalletOption? WalletFor(
        CategorizationProposal proposal, TransactionKind kind, IReadOnlyList<WalletOption> wallets, string defaultCurrency,
        out string failure)
    {
        if (proposal.WalletId is { } named)
        {
            var offered = wallets.FirstOrDefault(wallet => wallet.Id == named);
            failure = offered is null ? $"wallet {named} was not offered" : string.Empty;
            return offered;
        }

        var currency = StatedCurrency(proposal, kind) ?? defaultCurrency;
        var resolved = DefaultWalletOf(wallets, currency) ?? DefaultWalletOf(wallets, defaultCurrency);
        failure = resolved is null ? "no wallet to record into" : string.Empty;
        return resolved;
    }

    // A statement's lines are never read, so a stray line cannot pick a statement's wallet.
    static string? StatedCurrency(CategorizationProposal proposal, TransactionKind kind)
    {
        var lineCurrency = kind == TransactionKind.BalanceCheck ? null : proposal.Items.FirstOrDefault()?.CurrencyCode;
        return NonBlank(lineCurrency) ?? NonBlank(proposal.BalanceCurrency);
    }

    static WalletOption? DefaultWalletOf(IReadOnlyList<WalletOption> wallets, string currency) =>
        wallets.FirstOrDefault(wallet =>
            wallet.IsDefaultForCurrency && string.Equals(wallet.Currency.Value, currency, StringComparison.OrdinalIgnoreCase));

    static Money? StatementOf(CategorizationProposal proposal, WalletOption wallet, out string failure)
    {
        if (proposal.BalanceAmount is not { } amount)
        {
            failure = "a balance statement with no amount";
            return null;
        }

        var code = NonBlank(proposal.BalanceCurrency) ?? wallet.Currency.Value;
        if (Supported(code) is not { } currency)
        {
            failure = $"balance currency \"{code}\" is not one this ledger supports.";
            return null;
        }

        failure = string.Empty;
        return new Money(amount, currency);
    }

    static List<ResolvedLineItem>? MapItems(
        IReadOnlyList<ProposedLineItem> proposed,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        string walletCurrency,
        out string failure)
    {
        var items = new List<ResolvedLineItem>(proposed.Count);

        for (var index = 0; index < proposed.Count; index++)
        {
            var item = proposed[index];
            if (MapItem(item, offeredSlugs, offeredMerchantIds, walletCurrency, out var reason) is not { } resolved)
            {
                failure = $"Item {index + 1} (\"{item.Description}\"): {reason}";
                return null;
            }

            items.Add(resolved);
        }

        failure = string.Empty;
        return items;
    }

    static ResolvedLineItem? MapItem(
        ProposedLineItem item,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        string walletCurrency,
        out string reason)
    {
        var code = NonBlank(item.CurrencyCode) ?? walletCurrency;
        if (Supported(code) is not { } currency)
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
            NonBlank(item.MerchantName) is { } merchantName ? Truncate(merchantName, MaxMerchantNameLength) : null);
    }

    static bool TryParseDay(string? text, out DateOnly? day, out string failure)
    {
        day = null;
        failure = string.Empty;

        if (NonBlank(text) is not { } trimmed)
            return true;

        if (!DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            failure = $"occurred_on \"{text}\" is not an ISO date (YYYY-MM-DD).";
            return false;
        }

        day = parsed;
        return true;
    }

    static CurrencyCode? Supported(string code)
    {
        var currency = CurrencyCode.Supported.FirstOrDefault(
            supported => string.Equals(supported.Value, code, StringComparison.OrdinalIgnoreCase));
        return currency.Value is null ? null : currency;
    }

    static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    static string Truncate(string text, int maxLength) => text.Length > maxLength ? text[..maxLength] : text;
}
