using Noof.Ledger.Application.Fx;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting.Summary;

// Spec §2, SS-15: every line is valued first in its wallet's currency - a charge split over the lines it prices, any
// other foreign line converted "≈" (A-4) - then in the summary's currency at its own day's rate, and only then added.
// Nothing is rounded here; a figure is rounded only when it is formatted.
internal static class MonthlySummaryCalculator
{
    const int MaxHighlights = 3;
    const int MaxMerchants = 5;
    const int MaxRecords = 5;
    const string TransferFeeLabel = "Transfer fee";

    public static MonthlySummary Calculate(
        SummaryRows rows, RateTable rates, SummaryPeriod period, SummaryScope scope, CurrencyCode currency)
    {
        var wallet = scope.WalletId is { } walletId
            ? rows.Wallets.FirstOrDefault(candidate => candidate.Id == walletId)
                ?? throw new KeyNotFoundException($"Wallet {walletId} is not one the summary knows.")
            : null;
        var target = wallet?.Currency ?? currency;
        var averageMonths = SummaryWindows.AverageMonths(
            period, wallet is null ? rows.Wallets.Select(candidate => candidate.HistoryStart).Min() : wallet.HistoryStart);
        var items = Items(rows, rates, period, wallet, target);
        var spending = items.Where(item => item is { InTarget: not null, IsReceived: false }).ToList();
        var receiving = items.Where(item => item is { InTarget: not null, IsReceived: true }).ToList();
        var spent = Figure(spending, averageMonths);
        var received = Figure(receiving, averageMonths);
        var categories = CategoryFigures(spending, averageMonths);
        var movements = rows.Transfers.Where(transfer => period.Contains(transfer.OccurredOn)).ToList();
        var valued = items.Select(item => item.InTarget ?? item.InWallet).OfType<RateConversion>().ToList();
        var transfers = rows.Transfers.ToDictionary(transfer => transfer.TransactionId);
        var noRates = wallet is null && rates.IsEmpty && items.Exists(item => item is { Window: 0, InTarget: null });

        return new MonthlySummary(
            Period: period,
            Scope: scope,
            Currency: target,
            WalletName: wallet?.Name,
            Spent: spent,
            Received: received,
            Net: new SummaryAmount(
                received.Amount - spent.Amount, received.Previous - spent.Previous, received.Average - spent.Average),
            MovedOut: wallet is null ? null : MovedOut(movements, wallet.Id),
            MovedIn: wallet is null ? null : MovedIn(movements, wallet.Id),
            AverageMonths: averageMonths,
            Highlights: Highlights(categories),
            Categories: [.. categories.Where(category => spending.Exists(
                item => item.Window == 0 && item.Line.CategoryName == category.CategoryName))],
            TopMerchants: TopMerchants(spending, transfers),
            LargestRecords: LargestRecords(spending, rows.Lines, transfers),
            Wallets: wallet is null ? WalletLines(items, rows.Wallets) : [],
            NotConverted: NotConverted(items, everything: noRates),
            WalletOptions: WalletOptions(rows, scope),
            AnyApproximate: valued.Exists(value => value.Approximate),
            OldestRateDate: valued.Select(value => value.OldestRateDate).Min(),
            NewestRateDate: valued.Select(value => value.NewestRateDate).Max(),
            HasAnyRecords: items.Exists(item => item.Window == 0)
                || (wallet is not null && movements.Exists(
                    transfer => transfer.FromWalletId == wallet.Id || transfer.ToWalletId == wallet.Id)),
            NoRates: noRates);
    }

    // Window 0 is the month, 1..3 the months before it; a line in none of them (a later day of an earlier month while
    // the current one runs) does not count.
    static List<Item> Items(SummaryRows rows, RateTable rates, SummaryPeriod period, SummaryWallet? wallet, CurrencyCode target)
    {
        var windows = Enumerable.Range(0, SummaryWindows.ComparedMonths + 1)
            .Select(months => SummaryWindows.MonthsBack(period, months))
            .ToList();
        var shares = ChargeShares(rows.Lines);

        return
        [
            .. rows.Lines
                .Where(line => wallet is null || line.WalletId == wallet.Id)
                .Select(line => (Line: line, Window: windows.FindIndex(window => window.Contains(line.OccurredOn))))
                .Where(placed => placed.Window >= 0)
                .Select(placed => Value(placed.Line, placed.Window, shares, rates, target)),
        ];
    }

    static Item Value(
        SummaryLineRow line, int window, Dictionary<SummaryLineRow, decimal> shares, RateTable rates, CurrencyCode target)
    {
        var sign = line.Kind switch
        {
            SummaryLineKind.Spent or SummaryLineKind.Received => 1m,
            SummaryLineKind.Refund => -1m,
            _ => throw new ArgumentOutOfRangeException(nameof(line), line.Kind, "A summary line must be spent, received or a refund."),
        };
        var inWallet = shares.TryGetValue(line, out var share)
            ? new RateConversion(share, false, null, null)
            : InWalletCurrency(line, rates);
        var signed = inWallet is { } value ? value with { Amount = sign * value.Amount } : (RateConversion?)null;
        var inTarget = signed is { } walletAmount
            ? Then(walletAmount, rates.TryConvert(walletAmount.Amount, line.WalletCurrency, target, line.OccurredOn))
            : null;

        return new Item(line, window, sign * line.Amount, signed, inTarget);
    }

    // A-4: a foreign line no charge prices is converted at its day's mid rate and always marked "≈".
    static RateConversion? InWalletCurrency(SummaryLineRow line, RateTable rates) =>
        line.Currency == line.WalletCurrency
            ? new RateConversion(line.Amount, false, null, null)
            : rates.TryConvert(line.Amount, line.Currency, line.WalletCurrency, line.OccurredOn) is { } converted
                ? converted with { Approximate = true }
                : null;

    static RateConversion? Then(RateConversion first, RateConversion? next) => next is { } second
        ? new RateConversion(
            second.Amount,
            first.Approximate || second.Approximate,
            Earlier(first.OldestRateDate, second.OldestRateDate),
            Later(first.NewestRateDate, second.NewestRateDate))
        : null;

    // A lifted comparison with a null right is false, so left stays.
    static DateOnly? Earlier(DateOnly? left, DateOnly? right) => left is null || right < left ? right : left;

    static DateOnly? Later(DateOnly? left, DateOnly? right) => left is null || right > left ? right : left;

    // Spec §2: a charge replaces the Principal lines it prices, split in proportion to their amounts so the parts add
    // up to it exactly. Every caller splitting a charge passes the weights in ordinal order, so Home and the summary
    // give the same cent to the same line. Keyed by reference: line_items has no unique (transaction_id, ordinal), so
    // two lines can be equal by value and still each need their own share.
    static Dictionary<SummaryLineRow, decimal> ChargeShares(IReadOnlyList<SummaryLineRow> lines)
    {
        var shares = new Dictionary<SummaryLineRow, decimal>(ReferenceEqualityComparer.Instance);
        var priced = lines
            .Where(line => line is { Kind: SummaryLineKind.Spent, Role: EntryRole.Principal, ChargedAmount: not null })
            .GroupBy(line => (line.TransactionId, line.Currency));

        foreach (var group in priced)
        {
            List<SummaryLineRow> ordered = [.. group.OrderBy(line => line.Ordinal)];
            var charge = new Money(ordered[0].ChargedAmount.GetValueOrDefault(), ordered[0].WalletCurrency);
            var parts = charge.Allocate([.. ordered.Select(line => line.Amount)]);
            for (var index = 0; index < ordered.Count; index++)
                shares[ordered[index]] = parts[index].Amount;
        }

        return shares;
    }

    // A-5: a window before the scope's history start is unknown, not zero, so it is neither the previous month nor
    // part of the average. The windows counted are always the most recent ones, so the previous month is known exactly
    // when at least one is.
    static SummaryAmount Figure(IEnumerable<Item> items, int averageMonths)
    {
        var byWindow = new decimal[SummaryWindows.ComparedMonths + 1];
        foreach (var item in items)
            byWindow[item.Window] += item.Amount;

        return averageMonths == 0
            ? new SummaryAmount(byWindow[0], null, null)
            : new SummaryAmount(byWindow[0], byWindow[1], byWindow.Skip(1).Take(averageMonths).Sum() / averageMonths);
    }

    static List<SummaryCategory> CategoryFigures(List<Item> spending, int averageMonths) =>
    [
        .. spending
            .GroupBy(item => item.Line.CategoryName, StringComparer.Ordinal)
            .Select(group => new SummaryCategory(group.Key, Figure(group, averageMonths)))
            .OrderByDescending(category => category.Amount.Amount)
            .ThenBy(category => category.CategoryName, StringComparer.Ordinal),
    ];

    // Spec §2 "Contents" compares with the previous month when there is no average, but under A-5 the previous month
    // is known exactly when an average is: a scope whose history starts this month has nothing to compare with, so no
    // highlights, rather than every category reading as a change from zero.
    static List<SummaryHighlight> Highlights(List<SummaryCategory> categories) =>
    [
        .. categories
            .Where(category => category.Amount.Average is not null)
            .Select(category => new SummaryHighlight(
                category.CategoryName,
                category.Amount.Amount,
                category.Amount.Amount - category.Amount.Average.GetValueOrDefault(),
                HighlightBase.Average))
            .Where(highlight => highlight.Change != 0)
            .OrderByDescending(highlight => Math.Abs(highlight.Change))
            .ThenBy(highlight => highlight.CategoryName, StringComparer.Ordinal)
            .Take(MaxHighlights),
    ];

    static List<SummaryMerchant> TopMerchants(List<Item> spending, Dictionary<Guid, SummaryTransferRow> transfers) =>
    [
        .. spending
            .Where(item => item.Window == 0)
            .Select(item => MerchantOf(item.Line, transfers) is { } merchant ? new MerchantSpend(merchant, item) : null)
            .OfType<MerchantSpend>()
            .GroupBy(spend => spend.Merchant, StringComparer.Ordinal)
            .Select(group => new SummaryMerchant(
                group.Key,
                group.Sum(spend => spend.Item.Amount),
                group.Select(spend => spend.Item.Line.TransactionId).Distinct().Count()))
            .Where(merchant => merchant.Amount > 0)
            .OrderByDescending(merchant => merchant.Amount)
            .ThenBy(merchant => merchant.MerchantName, StringComparer.Ordinal)
            .Take(MaxMerchants),
    ];

    // A record's spent amount is its spending lines in the scope, its charge and fee included; a refund record is
    // negative and never among the largest.
    static List<SummaryRecord> LargestRecords(
        List<Item> spending, IReadOnlyList<SummaryLineRow> lines, Dictionary<Guid, SummaryTransferRow> transfers)
    {
        var firstLines = lines
            .GroupBy(line => line.TransactionId)
            .ToDictionary(group => group.Key, group => group.OrderBy(line => line.Ordinal).First());

        return
        [
            .. spending
                .Where(item => item.Window == 0)
                .GroupBy(item => item.Line.TransactionId)
                .Select(group => new SummaryRecord(
                    group.Key,
                    group.First().Line.OccurredOn,
                    LabelOf(firstLines[group.Key], transfers.GetValueOrDefault(group.Key)),
                    group.Sum(item => item.Amount),
                    group.Any(item => item.InTarget?.Approximate == true)))
                .Where(record => record.Amount > 0)
                .OrderByDescending(record => record.Amount)
                .ThenBy(record => record.OccurredOn)
                .ThenBy(record => record.Label, StringComparer.Ordinal)
                .ThenBy(record => record.TransactionId)
                .Take(MaxRecords),
        ];
    }

    // P-7: a transfer's fee is labelled by its venue, else "Transfer fee"; any other record by its merchant, else by
    // its first line's description.
    static string LabelOf(SummaryLineRow firstLine, SummaryTransferRow? transfer) =>
        transfer is not null ? transfer.VenueName ?? TransferFeeLabel : firstLine.MerchantName ?? firstLine.Description;

    static string? MerchantOf(SummaryLineRow line, Dictionary<Guid, SummaryTransferRow> transfers) =>
        transfers.TryGetValue(line.TransactionId, out var transfer) ? transfer.VenueName : line.MerchantName;

    // SS-16, as LedgerPostings posts a transfer: a leg's principal is its stored amount without the fee inside it,
    // so a wallet's moved amount and its fee line add up to the leg as stored.
    static decimal MovedOut(List<SummaryTransferRow> transfers, Guid walletId) =>
        transfers
            .Where(transfer => transfer.FromWalletId == walletId)
            .Sum(transfer => transfer.From.Amount - (transfer.FeeLeg == TransferLeg.From ? transfer.Fee ?? 0m : 0m));

    static decimal MovedIn(List<SummaryTransferRow> transfers, Guid walletId) =>
        transfers
            .Where(transfer => transfer.ToWalletId == walletId)
            .Sum(transfer => transfer.To.Amount + (transfer.FeeLeg == TransferLeg.To ? transfer.Fee ?? 0m : 0m));

    static List<SummaryWalletLine> WalletLines(List<Item> items, IReadOnlyList<SummaryWallet> wallets)
    {
        var byId = wallets.ToDictionary(wallet => wallet.Id);

        return
        [
            .. items
                .Where(item => item is { Window: 0, InWallet: not null })
                .GroupBy(item => item.Line.WalletId)
                .Select(group => new SummaryWalletLine(
                    group.Key,
                    byId[group.Key].Name,
                    byId[group.Key].Currency,
                    group.Where(item => !item.IsReceived).Sum(item => item.InWallet?.Amount ?? 0m),
                    group.Where(item => item.IsReceived).Sum(item => item.InWallet?.Amount ?? 0m),
                    group.Any(item => item.InWallet?.Approximate == true)))
                .OrderBy(line => line.WalletName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(line => line.WalletId),
        ];
    }

    // Spec §2 "Not converted": what a missing rate kept out, per the currency it was still in. With no rates at all
    // the view is this block only, so it then holds everything the month counted - the target currency too.
    static List<SummaryNotConverted> NotConverted(List<Item> items, bool everything) =>
    [
        .. items
            .Where(item => item.Window == 0 && (everything || item.InTarget is null))
            .Select(item => item.InWallet is { } inWallet
                ? (Currency: item.Line.WalletCurrency, Amount: inWallet.Amount, item.IsReceived)
                : (Currency: item.Line.Currency, Amount: item.Signed, item.IsReceived))
            .GroupBy(left => left.Currency)
            .OrderBy(group => group.Key)
            .Select(group => new SummaryNotConverted(
                group.Key,
                group.Where(left => !left.IsReceived).Sum(left => left.Amount),
                group.Where(left => left.IsReceived).Sum(left => left.Amount))),
    ];

    // Spec P-10: a wallet with anything counted or moved in the months read, and the one selected.
    static List<SummaryWalletOption> WalletOptions(SummaryRows rows, SummaryScope scope)
    {
        HashSet<Guid> shown =
        [
            .. rows.Lines.Select(line => line.WalletId),
            .. rows.Transfers.Select(transfer => transfer.FromWalletId),
            .. rows.Transfers.Select(transfer => transfer.ToWalletId),
        ];
        if (scope.WalletId is { } selected)
            shown.Add(selected);

        return
        [
            .. rows.Wallets
                .Where(wallet => shown.Contains(wallet.Id))
                .OrderBy(wallet => wallet.Archived)
                .ThenBy(wallet => wallet.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(wallet => wallet.Id)
                .Select(wallet => new SummaryWalletOption(wallet.Id, wallet.Name, wallet.Currency, wallet.Archived)),
        ];
    }

    // InWallet and InTarget are signed - spending positive, a refund negative (SS-19), received positive - and null
    // where no rate converts the line. Signed is the line's own amount with that sign, for "Not converted".
    sealed record Item(SummaryLineRow Line, int Window, decimal Signed, RateConversion? InWallet, RateConversion? InTarget)
    {
        public bool IsReceived => Line.Kind == SummaryLineKind.Received;

        public decimal Amount => InTarget?.Amount ?? 0m;
    }

    sealed record MerchantSpend(string Merchant, Item Item);
}
