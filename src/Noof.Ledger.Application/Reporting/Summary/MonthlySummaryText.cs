using System.Globalization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting.Summary;

// Spec §4: the bot's message, the automatic summary and what both Explain buttons send to the model, so the three
// cannot drift. Budgeted, not merely capped: names are cut, lists capped, and only then does an over-long text lose
// its largest records, then its merchants, then its tail.
internal sealed class MonthlySummaryText : IMonthlySummaryText
{
    const int MaxLength = 3800;
    const int MaxNameLength = 24;
    const int ShownCategories = 8;
    const int ShownMerchants = 3;
    const int ShownRecords = 3;
    const int ShownWallets = 6;
    const string Separator = " · ";
    const string NoRatesLine = "No exchange rates yet — totals are shown per currency.";

    readonly int budget;

    public MonthlySummaryText()
        : this(MaxLength)
    {
    }

    // A capped real summary never nears MaxLength, so a smaller budget is how a test sees the reduction.
    internal MonthlySummaryText(int budget) => this.budget = budget;

    public string Render(MonthlySummary summary)
    {
        var text = Compose(summary, withMerchants: true, withLargest: true);
        if (text.Length > budget)
            text = Compose(summary, withMerchants: true, withLargest: false);
        if (text.Length > budget)
            text = Compose(summary, withMerchants: false, withLargest: false);

        return text.Length > budget ? text[..(budget - 1)] + "…" : text;
    }

    public string Highlight(SummaryHighlight highlight, CurrencyCode currency, int averageMonths)
    {
        var against = highlight.Against switch
        {
            HighlightBase.Average when averageMonths == 1 => "your monthly average",
            HighlightBase.Average => string.Create(CultureInfo.InvariantCulture, $"your {averageMonths}-month average"),
            HighlightBase.PreviousMonth => "last month",
            _ => throw new ArgumentOutOfRangeException(nameof(highlight), highlight.Against, "A highlight compares with the average or last month."),
        };
        var direction = highlight.Change < 0 ? "less" : "more";

        return $"{highlight.CategoryName}: {SummaryFormat.Amount(highlight.Amount)} {currency}, "
            + $"{SummaryFormat.Amount(Math.Abs(highlight.Change))} {direction} than {against}.";
    }

    // Spec A-6: a percentage only against a figure above zero; against zero, "new" when this month has anything.
    public string? Percent(decimal amount, decimal? baseline) => baseline switch
    {
        { } positive when positive > 0 => WholePercent((amount - positive) / positive * 100m),
        0m when amount != 0 => "new",
        _ => null,
    };

    string Compose(MonthlySummary summary, bool withMerchants, bool withLargest)
    {
        // Spec §2: with no rates at all the all-wallets view is the Not converted block and nothing else.
        if (summary.NoRates)
            return string.Join('\n', Header(summary), NoRatesLine, NotConvertedLine(summary.NotConverted));

        string?[] lines =
        [
            Header(summary),
            SpentLine(summary),
            ReceivedLine(summary),
            MovedLine(summary),
            .. summary.Highlights.Select(highlight =>
                "• " + Highlight(highlight with { CategoryName = Cut(highlight.CategoryName) }, summary.Currency, summary.AverageMonths)),
            CategoriesLine(summary.Categories),
            withMerchants ? MerchantsLine(summary.TopMerchants) : null,
            withLargest ? LargestLine(summary.LargestRecords) : null,
            WalletsLine(summary.Wallets),
            NotConvertedLine(summary.NotConverted),
            RatesLine(summary),
        ];

        return string.Join('\n', lines.OfType<string>());
    }

    static string Header(MonthlySummary summary)
    {
        string?[] parts =
        [
            SummaryFormat.Month(summary.Period.FirstDay),
            summary.Period.Finished ? null : SummaryFormat.Window(summary.Period),
            summary.WalletName is { } wallet ? Cut(wallet) : null,
            summary.Currency.Value,
        ];

        return string.Join(Separator, parts.OfType<string>());
    }

    string SpentLine(MonthlySummary summary)
    {
        var spent = summary.Spent;
        string?[] comparisons =
        [
            Percent(spent.Amount, spent.Previous) is { } previous ? $"vs {PreviousWindow(summary)} {previous}" : null,
            Percent(spent.Amount, spent.Average) is { } average ? $"vs {AverageLabel(summary.AverageMonths)} {average}" : null,
        ];

        return $"Spent {Mark(summary.AnyApproximate)}{SummaryFormat.Amount(spent.Amount)}{InParentheses(comparisons)}";
    }

    // Received gets no comparison here to keep the message short; net's is its signed change, never a percent (A-6).
    static string ReceivedLine(MonthlySummary summary)
    {
        var mark = Mark(summary.AnyApproximate);
        var net = summary.Net;
        string?[] comparison =
            [net.Previous is { } previous ? $"vs {PreviousWindow(summary)} {SummaryFormat.Signed(net.Amount - previous)}" : null];

        return $"Received {mark}{SummaryFormat.Amount(summary.Received.Amount)} · Net {mark}{SummaryFormat.Signed(net.Amount)}"
            + InParentheses(comparison);
    }

    static string? MovedLine(MonthlySummary summary) =>
        summary is { MovedOut: { } movedOut, MovedIn: { } movedIn } && (movedOut != 0 || movedIn != 0)
            ? $"Moved out {SummaryFormat.Amount(movedOut)} · Moved in {SummaryFormat.Amount(movedIn)}"
            : null;

    static string? CategoriesLine(IReadOnlyList<SummaryCategory> categories)
    {
        if (categories.Count == 0)
            return null;

        var shown = categories.Take(ShownCategories).Select(category => $"{Cut(category.CategoryName)} {SummaryFormat.Amount(category.Amount.Amount)}");
        var rest = categories.Skip(ShownCategories).ToList();
        string[] parts = rest.Count == 0
            ? [.. shown]
            : [.. shown, $"Other {SummaryFormat.Amount(rest.Sum(category => category.Amount.Amount))}"];

        return "Categories: " + string.Join(Separator, parts);
    }

    static string? MerchantsLine(IReadOnlyList<SummaryMerchant> merchants) =>
        merchants.Count == 0
            ? null
            : "Top merchants: " + string.Join(Separator, merchants.Take(ShownMerchants).Select(merchant =>
                string.Create(CultureInfo.InvariantCulture, $"{Cut(merchant.MerchantName)} {SummaryFormat.Amount(merchant.Amount)} ({merchant.Records})")));

    static string? LargestLine(IReadOnlyList<SummaryRecord> records) =>
        records.Count == 0
            ? null
            : "Largest: " + string.Join(Separator, records.Take(ShownRecords).Select(record =>
                $"{Mark(record.Approximate)}{SummaryFormat.Amount(record.Amount)} {Cut(record.Label)}"));

    static string? WalletsLine(IReadOnlyList<SummaryWalletLine> wallets)
    {
        if (wallets.Count == 0)
            return null;

        var shown = wallets.Take(ShownWallets).Select(wallet =>
            $"{Cut(wallet.WalletName)} {Mark(wallet.Approximate)}{SummaryFormat.Signed(-wallet.Spent)} / {SummaryFormat.Signed(wallet.Received)} {wallet.Currency}");
        var more = wallets.Count - ShownWallets;
        string[] parts = more > 0
            ? [.. shown, string.Create(CultureInfo.InvariantCulture, $"+{more} more on the dashboard")]
            : [.. shown];

        return "Wallets: " + string.Join(Separator, parts);
    }

    static string? NotConvertedLine(IReadOnlyList<SummaryNotConverted> notConverted) =>
        notConverted.Count == 0
            ? null
            : "Not converted: " + string.Join(Separator, notConverted.Select(left =>
                $"{SummaryFormat.Signed(-left.Spent)} / {SummaryFormat.Signed(left.Received)} {left.Currency}"));

    static string? RatesLine(MonthlySummary summary) => (summary.OldestRateDate, summary.NewestRateDate) switch
    {
        ({ } oldest, { } newest) when oldest == newest => $"Rates of {SummaryFormat.Day(newest)} · exchangerate-api.com",
        ({ } oldest, { } newest) => $"Rates of {SummaryFormat.Day(oldest)}–{SummaryFormat.Day(newest)} · exchangerate-api.com",
        _ => null,
    };

    static string PreviousWindow(MonthlySummary summary) => SummaryFormat.Window(SummaryWindows.MonthsBack(summary.Period, 1));

    static string AverageLabel(int months) =>
        months == 1 ? "monthly avg" : string.Create(CultureInfo.InvariantCulture, $"{months}-month avg");

    static string InParentheses(IEnumerable<string?> parts) =>
        parts.OfType<string>().ToList() is { Count: > 0 } shown ? $" ({string.Join(", ", shown)})" : "";

    static string WholePercent(decimal percent) => decimal.Round(percent, 0, MidpointRounding.AwayFromZero) switch
    {
        > 0m and var up => string.Create(CultureInfo.InvariantCulture, $"+{up:F0}%"),
        < 0m and var down => string.Create(CultureInfo.InvariantCulture, $"{down:F0}%"),
        _ => "0%",
    };

    static string Mark(bool approximate) => approximate ? "≈" : "";

    static string Cut(string name) => name.Length <= MaxNameLength ? name : name[..(MaxNameLength - 1)] + "…";
}
