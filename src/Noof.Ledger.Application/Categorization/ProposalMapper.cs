using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

// Maps, never judges. Amount is already a decimal by the time it reaches here — the model answered a
// JSON number and System.Text.Json read it straight in, so there is no amount parsing left to do.
// Whether a figure is plausible, or appears in the message at all, is for the person to see in the
// echo and correct there (D1, docs/decisions/p2-1-quote-and-verify-removed.md). Do not add a sanity bound or a verbatim
// check here. The same holds for the wallet and the kind: an offered wallet id and a known kind map,
// and whether income "looks like" income is not this class's question (M3, M9). A transfer fails only for the reasons
// its own rules name (spec §2): a missing received amount, one wallet on both sides, a leg in a wallet of another
// currency, a rate or a fee that cannot be placed, an amount that is not positive.
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
        out string failure,
        out RecordFailureReason reason)
    {
        mapped = new MappedProposal([], null);
        reason = RecordFailureReason.None;

        if (KindOf(proposal.Kind) is not { } kind)
        {
            failure = $"kind \"{proposal.Kind}\" is not expense, income, balance or transfer.";
            return false;
        }

        if (kind == TransactionKind.Transfer)
            return TryMapTransfer(proposal, wallets, out mapped, out failure, out reason);

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
        ProposedKind.Transfer => TransactionKind.Transfer,
        _ => null,
    };

    // Spec §2: legs (rule 1), then the settlement (rules 2-3) in Domain. A transfer has no line items by definition.
    static bool TryMapTransfer(
        CategorizationProposal proposal, IReadOnlyList<WalletOption> wallets,
        out MappedProposal mapped, out string failure, out RecordFailureReason reason)
    {
        mapped = new MappedProposal([], null);
        reason = RecordFailureReason.None;

        if (proposal.Transfer is not { } transfer)
        {
            failure = "a transfer with no transfer object";
            return false;
        }

        if (RequestOf(transfer, out failure) is not { } request)
            return false;

        if (!TryLegWallet(transfer.FromWalletId, request.From.Currency, WalletPaymentDefault.Card, wallets, out var from, out failure, out reason))
            return false;

        if (!TryLegWallet(transfer.ToWalletId, request.ToCurrency, WalletPaymentDefault.Cash, wallets, out var to, out failure, out reason))
            return false;

        if (from.Id == to.Id)
            return Fails(RecordFailureReason.SameWallet, out failure, out reason);

        if (!request.TrySettle(out var settled, out var settlement))
            return Fails(settlement, out failure, out reason);

        if (!TryParseDay(proposal.OccurredOn, out var occurredOn, out failure))
            return false;

        // Amendment 24: a valid stated rate is kept whenever it was said, even beside a said received amount (which
        // still wins for the settlement), so a date-only correction answered back unchanged keeps it. A rate that could
        // not convert between the legs - ignored by the settlement beside a said amount - is not kept.
        mapped = new MappedProposal(
            [], occurredOn, TransactionKind.Transfer, from.Id,
            Transfer: new TransferFacts(
                from.Id, settled.From, to.Id, settled.To, settled.Fee, settled.FeeLeg, request.ConvertingRate));
        failure = string.Empty;
        return true;
    }

    static TransferRequest? RequestOf(ProposedTransfer transfer, out string failure)
    {
        if (Supported(transfer.FromCurrency) is not { } fromCurrency)
            return Unmapped($"transfer from_currency \"{transfer.FromCurrency}\" is not one this ledger supports.", out failure);

        if (Supported(transfer.ToCurrency) is not { } toCurrency)
            return Unmapped($"transfer to_currency \"{transfer.ToCurrency}\" is not one this ledger supports.", out failure);

        ExchangeRate? rate = null;
        if (transfer.Rate is { } saidRate)
        {
            if (Supported(saidRate.BaseCurrency) is not { } baseCurrency)
                return Unmapped($"rate currency \"{saidRate.BaseCurrency}\" is not one this ledger supports.", out failure);

            if (Supported(saidRate.QuoteCurrency) is not { } quoteCurrency)
                return Unmapped($"rate currency \"{saidRate.QuoteCurrency}\" is not one this ledger supports.", out failure);

            rate = new ExchangeRate(baseCurrency, saidRate.QuoteAmount, quoteCurrency);
        }

        TransferFeeRequest? fee = null;
        if (transfer.Fee is { } saidFee)
        {
            if (Supported(saidFee.Currency) is not { } feeCurrency)
                return Unmapped($"fee currency \"{saidFee.Currency}\" is not one this ledger supports.", out failure);

            if (LegFor(saidFee, feeCurrency, fromCurrency, toCurrency) is not { } leg)
                return Unmapped($"fee leg \"{saidFee.Leg}\" is not from or to.", out failure);

            fee = new TransferFeeRequest(new Money(saidFee.Amount, feeCurrency), leg, saidFee.Included);
        }

        failure = string.Empty;
        return new TransferRequest(new Money(transfer.FromAmount, fromCurrency), toCurrency, transfer.ToAmount, rate, fee);
    }

    static TransferRequest? Unmapped(string why, out string failure)
    {
        failure = why;
        return null;
    }

    // Amendment 23: a fee in exactly one side's currency is on that side, whatever leg the model named. Only a fee in
    // both sides' currency (a same-currency transfer) or in neither takes the model's leg.
    static TransferLeg? LegFor(ProposedFee fee, CurrencyCode feeCurrency, CurrencyCode from, CurrencyCode to) =>
        (feeCurrency == from, feeCurrency == to) switch
        {
            (true, false) => TransferLeg.From,
            (false, true) => TransferLeg.To,
            _ => LegOf(fee.Leg),
        };

    static TransferLeg? LegOf(string leg) => leg.ToLowerInvariant() switch
    {
        ProposedLeg.From => TransferLeg.From,
        ProposedLeg.To => TransferLeg.To,
        _ => null,
    };

    // Spec §2 rule 1: a named wallet as named; otherwise (spec A-27) the source takes its currency's card default and the
    // destination its cash default, each else that currency's default - a bare "снял 10000" takes from the card and
    // lands in the cash instead of putting both legs on one wallet. Never the default currency's wallet, the fallback a
    // spending gets: a leg can only sit in a wallet of its currency.
    static bool TryLegWallet(
        Guid? named, CurrencyCode currency, WalletPaymentDefault unnamedTakes, IReadOnlyList<WalletOption> wallets,
        [NotNullWhen(true)] out WalletOption? wallet, out string failure, out RecordFailureReason reason)
    {
        reason = RecordFailureReason.None;

        if (named is { } id)
        {
            wallet = wallets.FirstOrDefault(offered => offered.Id == id);
            if (wallet is null)
            {
                failure = $"wallet {id} was not offered";
                return false;
            }
        }
        else
        {
            wallet = PaymentDefaultOf(wallets, unnamedTakes, currency) ?? DefaultWalletOf(wallets, currency.Value);
        }

        if (wallet is null || wallet.Currency != currency)
            return Fails(RecordFailureReason.LegCurrencyMismatch, out failure, out reason);

        failure = string.Empty;
        return true;
    }

    static WalletOption? PaymentDefaultOf(IReadOnlyList<WalletOption> wallets, WalletPaymentDefault method, CurrencyCode currency) =>
        wallets.FirstOrDefault(wallet => wallet.DefaultForPayment == method && wallet.Currency == currency);

    static bool Fails(RecordFailureReason why, out string failure, out RecordFailureReason reason)
    {
        reason = why;
        failure = why.ToString();
        return false;
    }

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
