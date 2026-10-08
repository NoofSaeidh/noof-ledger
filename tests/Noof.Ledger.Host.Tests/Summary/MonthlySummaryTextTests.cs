using System.Globalization;
using AwesomeAssertions;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Tests.Summary;

public class MonthlySummaryTextTests
{
    static readonly MonthlySummaryText Text = new();
    static readonly Guid WiseId = new("5a000000-0000-4000-8000-000000000001");
    static readonly Guid CashRsdId = new("5a000000-0000-4000-8000-000000000002");
    static readonly Guid GigatronRecord = new("7e000000-0000-4000-8000-000000000001");
    static readonly Guid MaxiRecord = new("7e000000-0000-4000-8000-000000000002");
    static readonly SummaryPeriod ThroughThe8th = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8), Finished: false);
    static readonly SummaryPeriod October = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Finished: true);

    static MonthlySummary Empty(SummaryPeriod period, SummaryScope scope, CurrencyCode currency, string? walletName = null) => new(
        Period: period,
        Scope: scope,
        Currency: currency,
        WalletName: walletName,
        Spent: new SummaryAmount(0m, 0m, null),
        Received: new SummaryAmount(0m, 0m, null),
        Net: new SummaryAmount(0m, 0m, null),
        MovedOut: scope.IsAllWallets ? null : 0m,
        MovedIn: scope.IsAllWallets ? null : 0m,
        AverageMonths: 0,
        Highlights: [],
        Categories: [],
        TopMerchants: [],
        LargestRecords: [],
        Wallets: [],
        NotConverted: [],
        WalletOptions: [],
        AnyApproximate: false,
        OldestRateDate: null,
        NewestRateDate: null,
        HasAnyRecords: false,
        NoRates: false);

    // The spec §4 sample with P-8's formatting. Spent vs 1–8 Sep: (412.30 - 368.125) / 368.125 = +12%;
    // vs the average: (412.30 - 434.00) / 434.00 = -5%; net's change 1687.70 - 1431.875 = 255.825 → +255.83.
    static MonthlySummary Sample() => Empty(ThroughThe8th, SummaryScope.AllWallets, CurrencyCode.Eur) with
    {
        Spent = new SummaryAmount(412.30m, 368.125m, 434.00m),
        Received = new SummaryAmount(2100.00m, 1800.00m, 2000.00m),
        Net = new SummaryAmount(1687.70m, 1431.875m, 1566.00m),
        AverageMonths = 3,
        Highlights = [new SummaryHighlight("Groceries", 180.10m, 40.00m, HighlightBase.Average)],
        Categories =
        [
            new SummaryCategory("Groceries", new SummaryAmount(180.10m, 150.00m, 140.10m)),
            new SummaryCategory("Cafés", new SummaryAmount(95.00m, 90.00m, 135.00m)),
            new SummaryCategory("Transport", new SummaryAmount(60.00m, 70.00m, 20.00m)),
            new SummaryCategory("Fees & Charges", new SummaryAmount(22.40m, 0m, 1.00m)),
        ],
        TopMerchants = [new SummaryMerchant("Maxi", 120.40m, 6), new SummaryMerchant("Gigatron", 89.00m, 1)],
        LargestRecords =
        [
            new SummaryRecord(GigatronRecord, new DateOnly(2026, 10, 6), "Gigatron", 89.00m, false),
            new SummaryRecord(MaxiRecord, new DateOnly(2026, 10, 2), "Maxi", 45.10m, false),
        ],
        Wallets =
        [
            new SummaryWalletLine(WiseId, "Wise", CurrencyCode.Eur, 250.00m, 2100.00m, false),
            new SummaryWalletLine(CashRsdId, "Cash RSD", CurrencyCode.Rsd, 14200.00m, 0m, false),
        ],
        OldestRateDate = new DateOnly(2026, 10, 3),
        NewestRateDate = new DateOnly(2026, 10, 7),
        HasAnyRecords = true,
    };

    const string SampleText = """
        October 2026 · 1–8 Oct · EUR
        Spent 412.30 (vs 1–8 Sep +12%, vs 3-month avg -5%)
        Received 2100.00 · Net +1687.70 (vs 1–8 Sep +255.83)
        • Groceries: 180.10 EUR, 40.00 more than your 3-month average.
        Categories: Groceries 180.10 · Cafés 95.00 · Transport 60.00 · Fees & Charges 22.40
        Top merchants: Maxi 120.40 (6) · Gigatron 89.00 (1)
        Largest: 89.00 Gigatron · 45.10 Maxi
        Wallets: Wise -250.00 / +2100.00 EUR · Cash RSD -14200.00 / 0.00 RSD
        Rates of 3 Oct–7 Oct · exchangerate-api.com
        """;

    // The source files are CRLF; the rendering joins lines with "\n" (Telegram's plain text).
    static string Lines(string text) => text.ReplaceLineEndings("\n");

    [Theory]
    [InlineData("412.30", "368.125", "+12%")]
    [InlineData("412.30", "434.00", "-5%")]
    [InlineData("100.50", "100", "+1%")]
    [InlineData("99.50", "100", "-1%")]
    [InlineData("99.60", "100", "0%")]
    [InlineData("30.00", "0", "new")]
    [InlineData("-15.00", "0", "new")]
    public void A_percent_is_against_a_positive_figure_whole_and_half_away_from_zero(string amount, string baseline, string expected) =>
        Text.Percent(decimal.Parse(amount, CultureInfo.InvariantCulture), decimal.Parse(baseline, CultureInfo.InvariantCulture))
            .Should().Be(expected);

    [Fact]
    public void There_is_no_percent_against_nothing_or_against_a_negative_figure()
    {
        Text.Percent(0m, 0m).Should().BeNull();
        Text.Percent(10m, null).Should().BeNull();
        Text.Percent(10m, -5m).Should().BeNull();
    }

    [Fact]
    public void A_highlight_is_one_sentence_against_the_average_or_last_month()
    {
        Text.Highlight(new SummaryHighlight("Groceries", 180.10m, 40.00m, HighlightBase.Average), CurrencyCode.Eur, 3)
            .Should().Be("Groceries: 180.10 EUR, 40.00 more than your 3-month average.");
        Text.Highlight(new SummaryHighlight("Cafés", 95.00m, -40.00m, HighlightBase.Average), CurrencyCode.Eur, 1)
            .Should().Be("Cafés: 95.00 EUR, 40.00 less than your monthly average.");
        Text.Highlight(new SummaryHighlight("Transport", 50.00m, 50.00m, HighlightBase.PreviousMonth), CurrencyCode.Rsd, 0)
            .Should().Be("Transport: 50.00 RSD, 50.00 more than last month.");

        var unknown = () => Text.Highlight(new SummaryHighlight("Transport", 50.00m, 50.00m, HighlightBase.Unknown), CurrencyCode.Eur, 0);
        unknown.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_current_month_renders_in_the_one_plain_text_layout()
    {
        Text.Render(Sample()).Should().Be(Lines(SampleText));
    }

    [Fact]
    public void A_finished_month_names_whole_months_and_a_wallet_adds_what_it_moved()
    {
        var summary = Empty(October, new SummaryScope(WiseId), CurrencyCode.Eur, "Wise") with
        {
            Spent = new SummaryAmount(1.50m, 0m, null),
            Net = new SummaryAmount(-1.50m, 0m, null),
            MovedOut = 100.00m,
            MovedIn = 50.00m,
            Categories = [new SummaryCategory("Fees & Charges", new SummaryAmount(1.50m, 0m, null))],
            TopMerchants = [new SummaryMerchant("Exchange Kiosk", 1.00m, 1)],
            LargestRecords =
            [
                new SummaryRecord(GigatronRecord, new DateOnly(2026, 10, 5), "Exchange Kiosk", 1.00m, false),
                new SummaryRecord(MaxiRecord, new DateOnly(2026, 10, 6), "Transfer fee", 0.50m, false),
            ],
            HasAnyRecords = true,
        };

        Text.Render(summary).Should().Be(Lines("""
            October 2026 · Wise · EUR
            Spent 1.50 (vs September new)
            Received 0.00 · Net -1.50 (vs September -1.50)
            Moved out 100.00 · Moved in 50.00
            Categories: Fees & Charges 1.50
            Top merchants: Exchange Kiosk 1.00 (1)
            Largest: 1.00 Exchange Kiosk · 0.50 Transfer fee
            """));
    }

    // A-5: a scope's first month has no earlier history, so it is compared with nothing — never "new".
    [Fact]
    public void A_first_month_with_no_earlier_history_has_no_comparison()
    {
        var summary = Empty(October, SummaryScope.AllWallets, CurrencyCode.Eur) with
        {
            Spent = new SummaryAmount(5.00m, null, null),
            Received = new SummaryAmount(0m, null, null),
            Net = new SummaryAmount(-5.00m, null, null),
            Categories = [new SummaryCategory("Cafés", new SummaryAmount(5.00m, null, null))],
            HasAnyRecords = true,
        };

        Text.Render(summary).Should().Be(Lines("""
            October 2026 · EUR
            Spent 5.00
            Received 0.00 · Net -5.00
            Categories: Cafés 5.00
            """));
    }

    [Fact]
    public void With_no_rates_the_text_is_the_not_converted_block_only()
    {
        var summary = Empty(ThroughThe8th, SummaryScope.AllWallets, CurrencyCode.Eur) with
        {
            Spent = new SummaryAmount(5.00m, 0m, null),
            Net = new SummaryAmount(-5.00m, 0m, null),
            Categories = [new SummaryCategory("Cafés", new SummaryAmount(5.00m, 0m, null))],
            Wallets =
            [
                new SummaryWalletLine(CashRsdId, "Cash RSD", CurrencyCode.Rsd, 1170.00m, 0m, false),
                new SummaryWalletLine(WiseId, "Wise", CurrencyCode.Eur, 5.00m, 0m, false),
            ],
            NotConverted =
            [
                new SummaryNotConverted(CurrencyCode.Eur, 5.00m, 0m),
                new SummaryNotConverted(CurrencyCode.Rsd, 1170.00m, 0m),
                new SummaryNotConverted(CurrencyCode.Usd, 11.00m, 55.00m),
            ],
            HasAnyRecords = true,
            NoRates = true,
        };

        Text.Render(summary).Should().Be(Lines("""
            October 2026 · 1–8 Oct · EUR
            No exchange rates yet — totals are shown per currency.
            Not converted: -5.00 / 0.00 EUR · -1170.00 / 0.00 RSD · -11.00 / +55.00 USD
            """));
    }

    [Fact]
    public void A_currency_without_a_rate_among_others_is_listed_under_the_full_summary()
    {
        var summary = Sample() with { NotConverted = [new SummaryNotConverted(CurrencyCode.Kzt, 15757.00m, 0m)] };

        var lines = Text.Render(summary).Split('\n');

        lines.Should().NotContain("No exchange rates yet — totals are shown per currency.");
        lines[^2].Should().Be("Not converted: -15757.00 / 0.00 KZT");
        lines.Should().Contain(line => line.StartsWith("Spent 412.30", StringComparison.Ordinal));
    }

    [Fact]
    public void Approximate_figures_carry_the_mark()
    {
        var summary = Empty(October, SummaryScope.AllWallets, CurrencyCode.Eur) with
        {
            Spent = new SummaryAmount(10.00m, 0m, null),
            Received = new SummaryAmount(50.00m, 0m, null),
            Net = new SummaryAmount(40.00m, 0m, null),
            Categories = [new SummaryCategory("Software", new SummaryAmount(10.00m, 0m, null))],
            TopMerchants = [new SummaryMerchant("JetBrains", 10.00m, 1)],
            LargestRecords = [new SummaryRecord(MaxiRecord, new DateOnly(2026, 10, 10), "JetBrains", 10.00m, true)],
            Wallets = [new SummaryWalletLine(WiseId, "Wise", CurrencyCode.Eur, 10.00m, 50.00m, true)],
            AnyApproximate = true,
            OldestRateDate = new DateOnly(2026, 10, 10),
            NewestRateDate = new DateOnly(2026, 10, 12),
            HasAnyRecords = true,
        };

        Text.Render(summary).Should().Be(Lines("""
            October 2026 · EUR
            Spent ≈10.00 (vs September new)
            Received ≈50.00 · Net ≈+40.00 (vs September +40.00)
            Categories: Software 10.00
            Top merchants: JetBrains 10.00 (1)
            Largest: ≈10.00 JetBrains
            Wallets: Wise ≈-10.00 / +50.00 EUR
            Rates of 10 Oct–12 Oct · exchangerate-api.com
            """));
    }

    [Fact]
    public void One_rate_date_is_named_once_and_one_month_of_history_is_a_monthly_average()
    {
        var oneDay = Text.Render(Sample() with { OldestRateDate = new DateOnly(2026, 10, 7) }).Split('\n');
        oneDay[^1].Should().Be("Rates of 7 Oct · exchangerate-api.com");

        var oneMonth = Text.Render(Sample() with { AverageMonths = 1, Highlights = [] }).Split('\n');
        oneMonth[1].Should().Be("Spent 412.30 (vs 1–8 Sep +12%, vs monthly avg -5%)");
    }

    [Fact]
    public void At_every_cap_with_the_longest_names_the_text_stays_within_3800_characters()
    {
        static string Name(string start) => start + new string('x', 128 - start.Length);
        const decimal Huge = 99999999.99m;
        var summary = Empty(ThroughThe8th, SummaryScope.AllWallets, CurrencyCode.Rsd) with
        {
            Spent = new SummaryAmount(Huge, 1m, 1m),
            Received = new SummaryAmount(Huge, 1m, 1m),
            Net = new SummaryAmount(-Huge, 1m, 1m),
            AverageMonths = 3,
            Highlights = [.. Enumerable.Range(1, 3).Select(n => new SummaryHighlight(Name($"Highlight {n} "), Huge, -Huge, HighlightBase.Average))],
            Categories = [.. Enumerable.Range(1, 10).Select(n => new SummaryCategory(Name($"Category {n} "), new SummaryAmount(Huge, Huge, Huge)))],
            TopMerchants = [.. Enumerable.Range(1, 5).Select(n => new SummaryMerchant(Name($"Merchant {n} "), Huge, 999))],
            LargestRecords = [.. Enumerable.Range(1, 5).Select(n => new SummaryRecord(Guid.NewGuid(), new DateOnly(2026, 10, n), Name($"Record {n} "), Huge, true))],
            Wallets = [.. Enumerable.Range(1, 8).Select(n => new SummaryWalletLine(Guid.NewGuid(), Name($"Wallet {n} "), CurrencyCode.Kzt, Huge, Huge, true))],
            NotConverted =
            [
                new SummaryNotConverted(CurrencyCode.Kzt, Huge, Huge),
                new SummaryNotConverted(CurrencyCode.Rub, Huge, Huge),
                new SummaryNotConverted(CurrencyCode.Usd, Huge, Huge),
            ],
            AnyApproximate = true,
            OldestRateDate = new DateOnly(2026, 9, 28),
            NewestRateDate = new DateOnly(2026, 10, 8),
            HasAnyRecords = true,
        };

        var text = Text.Render(summary);

        text.Length.Should().BeLessThanOrEqualTo(3800);
        text.Should().Contain("Top merchants:").And.Contain("Largest:", "the caps alone keep a real summary short");
        text.Should().Contain(Name("Wallet 1 ")[..23] + "…").And.NotContain(Name("Wallet 1 ")[..24]);
        text.Should().Contain(Name("Highlight 1 ")[..23] + "…");
        text.Should().Contain("+2 more on the dashboard");
        text.Should().Contain("Other 199999999.98");            // categories 9 and 10: 2 × 99999999.99
        text.Should().NotContain("Category 9 ").And.NotContain("Merchant 4 ").And.NotContain("Record 4 ").And.NotContain("Wallet 7 ");
    }

    [Fact]
    public void Over_its_budget_the_text_drops_the_largest_records_then_the_merchants_then_cuts_with_an_ellipsis()
    {
        var full = Text.Render(Sample());
        var lines = full.Split('\n');
        var largest = lines.Single(line => line.StartsWith("Largest:", StringComparison.Ordinal));
        var merchants = lines.Single(line => line.StartsWith("Top merchants:", StringComparison.Ordinal));
        var withoutLargest = string.Join('\n', lines.Where(line => line != largest));
        var withoutBoth = string.Join('\n', lines.Where(line => line != largest && line != merchants));

        new MonthlySummaryText(full.Length - 1).Render(Sample()).Should().Be(withoutLargest);
        new MonthlySummaryText(withoutLargest.Length - 1).Render(Sample()).Should().Be(withoutBoth);

        var cut = new MonthlySummaryText(40).Render(Sample());
        cut.Should().HaveLength(40).And.EndWith("…").And.StartWith(withoutBoth[..39]);
    }

    [Fact]
    public void The_text_is_the_same_under_any_server_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");

            Text.Render(Sample()).Should().Be(Lines(SampleText));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
