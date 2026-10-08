using AwesomeAssertions;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Domain;
using static Noof.Ledger.Host.Tests.Summary.SummaryRowsBuilder;

namespace Noof.Ledger.Host.Tests.Summary;

// Spec §2 on hand-built rows. Every expected figure is worked by hand in the comment beside it.
public class MonthlySummaryCalculatorTests
{
    static readonly CurrencyCode Eur = CurrencyCode.Eur;
    static readonly CurrencyCode Rsd = CurrencyCode.Rsd;
    static readonly CurrencyCode Kzt = CurrencyCode.Kzt;
    static readonly CurrencyCode Usd = CurrencyCode.Usd;

    static DateOnly Jul(int day) => new(2026, 7, day);
    static DateOnly Aug(int day) => new(2026, 8, day);
    static DateOnly Sep(int day) => new(2026, 9, day);
    static DateOnly Oct(int day) => new(2026, 10, day);

    static readonly SummaryPeriod October = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Finished: true);
    static readonly RateTable Usual = TestRates.Usual(new DateOnly(2026, 7, 1), new DateOnly(2026, 10, 31));

    static MonthlySummary ForWallet(SummaryRows rows, Guid walletId, RateTable? rates = null, SummaryPeriod? period = null) =>
        MonthlySummaryCalculator.Calculate(rows, rates ?? Usual, period ?? October, new SummaryScope(walletId), Eur);

    static MonthlySummary ForAllWallets(SummaryRows rows, CurrencyCode currency, RateTable? rates = null, SummaryPeriod? period = null) =>
        MonthlySummaryCalculator.Calculate(rows, rates ?? Usual, period ?? October, SummaryScope.AllWallets, currency);

    static IEnumerable<(string, decimal)> Amounts(MonthlySummary summary) =>
        summary.Categories.Select(category => (category.CategoryName, category.Amount.Amount));

    [Fact]
    public void A_wallets_spent_received_and_net_are_in_its_own_currency()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Oct(1));
        rows.Expense(WiseId, Oct(2), "Maxi", Line("Groceries", 30.00m));
        rows.Expense(WiseId, Oct(3), "Café Central", Line("Cafés", 12.50m));
        rows.Income(WiseId, Oct(5), "Employer", Line("Salary", 2000.00m));

        var summary = ForWallet(rows.Build(), WiseId);

        summary.Currency.Should().Be(Eur);
        summary.WalletName.Should().Be("Wise");
        summary.Spent.Amount.Should().Be(42.50m);                 // 30.00 + 12.50
        summary.Received.Amount.Should().Be(2000.00m);
        summary.Net.Amount.Should().Be(1957.50m);                 // 2000.00 - 42.50
        summary.Categories.Should().Equal(
            new SummaryCategory("Groceries", new SummaryAmount(30.00m, null, null)),
            new SummaryCategory("Cafés", new SummaryAmount(12.50m, null, null)));
        summary.Wallets.Should().BeEmpty();
        summary.MovedOut.Should().Be(0m);
        summary.MovedIn.Should().Be(0m);
        summary.AnyApproximate.Should().BeFalse();
        summary.OldestRateDate.Should().BeNull();
        summary.HasAnyRecords.Should().BeTrue();
    }

    [Fact]
    public void A_foreign_charge_is_split_across_its_lines_and_the_parts_add_up_to_the_charge()
    {
        var rows = new SummaryRowsBuilder().Wallet(KaspiId, "Kaspi", Kzt, Oct(1));
        rows.ChargedExpense(KaspiId, Oct(4), "Paddle", charged: 15601.00m,
            Line("Subscriptions", 20.00m, Usd, "SaaS"),
            Line("Subscriptions", 7.00m, Usd, "Cloud"),
            Line("Books", 3.00m, Usd, "Book"),
            Fee(156.00m, Kzt));

        var summary = ForWallet(rows.Build(), KaspiId);

        // 15601.00 KZT over 20:7:3 - Cloud 15601 × 7/30 = 3640.2333 → 3640.23, Book 15601 × 3/30 = 1560.10, SaaS takes
        // the rest 10400.67. Subscriptions = 10400.67 + 3640.23 = 14040.90; the fee line counts as itself.
        Amounts(summary).Should().Equal(("Subscriptions", 14040.90m), ("Books", 1560.10m), ("Fees & Charges", 156.00m));
        summary.Categories.Where(category => category.CategoryName != "Fees & Charges").Sum(category => category.Amount.Amount)
            .Should().Be(15601.00m);
        summary.Spent.Amount.Should().Be(15757.00m);              // 15601.00 + 156.00
        summary.Currency.Should().Be(Kzt);
        summary.AnyApproximate.Should().BeFalse("a charge is what the bank took, not a rate");
    }

    [Fact]
    public void A_foreign_discount_line_takes_its_negative_share_of_the_charge()
    {
        var rows = new SummaryRowsBuilder().Wallet(KaspiId, "Kaspi", Kzt, Oct(1));
        rows.ChargedExpense(KaspiId, Oct(4), "Tech Store", charged: 2500.00m,
            Line("Electronics", 30.00m, Usd, "Headphones"),
            Line("Discounts", -5.00m, Usd, "Coupon"));

        var summary = ForWallet(rows.Build(), KaspiId);

        // 2500.00 KZT over 30.00 and -5.00 USD (sum 25.00): Coupon 2500 × -5/25 = -500.00, Headphones the rest 3000.00.
        Amounts(summary).Should().Equal(("Electronics", 3000.00m), ("Discounts", -500.00m));
        summary.Spent.Amount.Should().Be(2500.00m);
    }

    [Fact]
    public void Two_identical_foreign_lines_under_one_charge_each_keep_their_own_share()
    {
        // line_items has no unique (transaction_id, ordinal), so two rows can be equal field for field.
        var recordId = Guid.NewGuid();
        var book = new SummaryLineRow(recordId, Oct(4), KaspiId, Kzt, SummaryLineKind.Spent, EntryRole.Principal, 0,
            "Books", "Paddle", "Book", 3.00m, Usd, 1000.00m);
        var rows = new SummaryRows(
            [new SummaryWallet(KaspiId, "Kaspi", Kzt, false, Oct(1))],
            [book, book with { }, book with { Ordinal = 1, CategoryName = "Subscriptions", Description = "SaaS" }],
            []);

        var summary = ForWallet(rows, KaspiId);

        // 1000.00 KZT over 3:3:3 - each 1000 × 3/9 = 333.333… cut to 333.33, 999.99 in all; the three tie on the
        // fraction cut off, so the first book takes the cent: 333.34. Books = 333.34 + 333.33 = 666.67.
        Amounts(summary).Should().Equal(("Books", 666.67m), ("Subscriptions", 333.33m));
        summary.Spent.Amount.Should().Be(1000.00m);
    }

    [Fact]
    public void A_transfer_fee_lands_on_its_legs_wallet_and_spent_plus_moved_is_the_stored_leg()
    {
        var rows = new SummaryRowsBuilder()
            .Wallet(WiseId, "Wise", Eur, Oct(1))
            .Wallet(CashRsdId, "Cash RSD", Rsd, null);
        // An exchange with its 1.00 EUR fee inside the From leg, then one with its 0.50 EUR fee inside the To leg.
        rows.Transfer(Oct(5), WiseId, 101.00m, CashRsdId, 11700.00m, TransferLeg.From, 1.00m, "Exchange Kiosk");
        rows.Transfer(Oct(6), CashRsdId, 5850.00m, WiseId, 49.50m, TransferLeg.To, 0.50m);
        var built = rows.Build();

        var wise = ForWallet(built, WiseId);
        wise.Spent.Amount.Should().Be(1.50m);       // both fees are Wise's: 1.00 on its From leg, 0.50 on its To leg
        wise.MovedOut.Should().Be(100.00m);         // 101.00 stored - the 1.00 fee: moved + fee = 101.00, the leg
        wise.MovedIn.Should().Be(50.00m);           // 49.50 stored + the 0.50 fee: moved - fee = 49.50, the leg
        wise.Net.Amount.Should().Be(-1.50m);
        Amounts(wise).Should().Equal(("Fees & Charges", 1.50m));

        var cash = ForWallet(built, CashRsdId);
        cash.Currency.Should().Be(Rsd);
        cash.Spent.Amount.Should().Be(0m);
        cash.MovedIn.Should().Be(11700.00m);        // no fee on this To leg
        cash.MovedOut.Should().Be(5850.00m);        // no fee on this From leg
        cash.Categories.Should().BeEmpty();
    }

    [Fact]
    public void All_wallets_leave_movements_out()
    {
        var rows = new SummaryRowsBuilder()
            .Wallet(WiseId, "Wise", Eur, Oct(1))
            .Wallet(CashRsdId, "Cash RSD", Rsd, null);
        rows.Transfer(Oct(5), WiseId, 101.00m, CashRsdId, 11700.00m, TransferLeg.From, 1.00m, "Exchange Kiosk");
        rows.Transfer(Oct(6), CashRsdId, 5850.00m, WiseId, 49.50m, TransferLeg.To, 0.50m);

        var summary = ForAllWallets(rows.Build(), Eur);

        summary.MovedOut.Should().BeNull();
        summary.MovedIn.Should().BeNull();
        summary.Spent.Amount.Should().Be(1.50m);
        summary.Wallets.Should().Equal(new SummaryWalletLine(WiseId, "Wise", Eur, 1.50m, 0m, false));
    }

    [Fact]
    public void A_refund_lowers_its_categorys_spending_below_zero_and_leaves_net_unchanged()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Oct(1));
        rows.Expense(WiseId, Oct(2), "Maxi", Line("Groceries", 30.00m));
        rows.Refund(WiseId, Oct(9), "Maxi", Line("Groceries", 45.00m));
        rows.Income(WiseId, Oct(5), "Employer", Line("Salary", 2000.00m));

        var summary = ForWallet(rows.Build(), WiseId);

        Amounts(summary).Should().Equal(("Groceries", -15.00m));  // 30.00 - 45.00
        summary.Spent.Amount.Should().Be(-15.00m);
        summary.Received.Amount.Should().Be(2000.00m);           // the refund is not received
        // Counted as received it would be (2000.00 + 45.00) - 30.00 = 2015.00 too: SS-19 moves it, net stays.
        summary.Net.Amount.Should().Be(2015.00m);
    }

    [Fact]
    public void A_foreign_line_with_no_charge_and_a_foreign_income_convert_at_their_days_rate_marked_approximate()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Oct(1));
        rows.Expense(WiseId, Oct(10), "JetBrains", Line("Software", 11.00m, Usd));
        rows.Income(WiseId, Oct(12), null, Line("Other income", 55.00m, Usd));

        var summary = ForWallet(rows.Build(), WiseId);

        summary.Spent.Amount.Should().Be(10.00m);                // 11.00 USD / 1.10
        summary.Received.Amount.Should().Be(50.00m);             // 55.00 USD / 1.10
        summary.AnyApproximate.Should().BeTrue("A-4 marks every such line, even at a same-day rate");
        summary.OldestRateDate.Should().Be(Oct(10));
        summary.NewestRateDate.Should().Be(Oct(12));
        summary.NotConverted.Should().BeEmpty();
    }

    [Fact]
    public void Without_a_rate_for_its_currency_a_foreign_line_is_not_converted()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Oct(1));
        rows.Expense(WiseId, Oct(10), "JetBrains", Line("Software", 11.00m, Usd));
        rows.Income(WiseId, Oct(12), null, Line("Other income", 55.00m, Usd));
        var noUsd = new RateTable(TestRates.Daily(Jul(1), Oct(31), (Rsd, 117.00m), (Kzt, 520.00m)));

        var summary = ForWallet(rows.Build(), WiseId, noUsd);

        summary.NotConverted.Should().Equal(new SummaryNotConverted(Usd, 11.00m, 55.00m));
        summary.Spent.Amount.Should().Be(0m);
        summary.Received.Amount.Should().Be(0m);
        summary.Categories.Should().BeEmpty();
        summary.AnyApproximate.Should().BeFalse();
        summary.HasAnyRecords.Should().BeTrue();
        summary.NoRates.Should().BeFalse("a wallet's own view never needs a rate to add itself up");
    }

    [Fact]
    public void With_no_rate_at_all_all_wallets_show_every_currency_on_its_own()
    {
        var rows = new SummaryRowsBuilder()
            .Wallet(WiseId, "Wise", Eur, Oct(1))
            .Wallet(CashRsdId, "Cash RSD", Rsd, Oct(1));
        rows.Expense(WiseId, Oct(7), "Café Central", Line("Cafés", 5.00m));
        rows.Expense(CashRsdId, Oct(3), "Maxi", Line("Groceries", 1170.00m, Rsd));
        rows.Income(CashRsdId, Oct(4), "Employer", Line("Salary", 2340.00m, Rsd));

        var summary = ForAllWallets(rows.Build(), Eur, new RateTable([]));

        summary.NoRates.Should().BeTrue();
        // Every item of the month per currency, EUR included, since the view is this block only (spec §2).
        summary.NotConverted.Should().Equal(
            new SummaryNotConverted(Eur, 5.00m, 0m),
            new SummaryNotConverted(Rsd, 1170.00m, 2340.00m));
        summary.OldestRateDate.Should().BeNull();
    }

    [Fact]
    public void With_no_rate_at_all_but_nothing_to_convert_the_summary_is_whole()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Oct(1));
        rows.Expense(WiseId, Oct(7), "Café Central", Line("Cafés", 5.00m));

        var summary = ForAllWallets(rows.Build(), Eur, new RateTable([]));

        summary.NoRates.Should().BeFalse();
        summary.NotConverted.Should().BeEmpty();
        summary.Spent.Amount.Should().Be(5.00m);
    }

    [Fact]
    public void All_wallets_convert_each_amount_at_its_own_days_rate()
    {
        var rows = new SummaryRowsBuilder()
            .Wallet(WiseId, "Wise", Eur, Oct(1))
            .Wallet(CashRsdId, "Cash RSD", Rsd, Oct(1));
        rows.Expense(CashRsdId, Oct(3), "Maxi", Line("Groceries", 1170.00m, Rsd));
        rows.Expense(CashRsdId, Oct(7), "Maxi", Line("Groceries", 1172.00m, Rsd));
        rows.Expense(WiseId, Oct(7), "Café Central", Line("Cafés", 5.00m));
        var built = rows.Build();
        var twoDays = new RateTable([new FxRate(Rsd, Oct(3), 117.00m), new FxRate(Rsd, Oct(7), 117.20m)]);

        var eur = ForAllWallets(built, Eur, twoDays);

        eur.Spent.Amount.Should().Be(25.00m);      // 1170.00 / 117.00 + 1172.00 / 117.20 + 5.00 = 10.00 + 10.00 + 5.00
        Amounts(eur).Should().Equal(("Groceries", 20.00m), ("Cafés", 5.00m));
        eur.OldestRateDate.Should().Be(Oct(3));
        eur.NewestRateDate.Should().Be(Oct(7));
        eur.AnyApproximate.Should().BeFalse();
        eur.Wallets.Should().Equal(
            new SummaryWalletLine(CashRsdId, "Cash RSD", Rsd, 2342.00m, 0m, false),   // 1170.00 + 1172.00, its own RSD
            new SummaryWalletLine(WiseId, "Wise", Eur, 5.00m, 0m, false));

        var rsd = ForAllWallets(built, Rsd, twoDays);

        rsd.Currency.Should().Be(Rsd);
        rsd.Spent.Amount.Should().Be(2928.00m);    // 1170.00 + 1172.00 + 5.00 × 117.20 (586.00)
        rsd.OldestRateDate.Should().Be(Oct(7));
        rsd.NewestRateDate.Should().Be(Oct(7));
    }

    [Fact]
    public void A_rate_more_than_three_days_old_marks_the_converted_figures_approximate()
    {
        var rows = new SummaryRowsBuilder().Wallet(CashRsdId, "Cash RSD", Rsd, Oct(1));
        rows.Expense(CashRsdId, Oct(10), "Maxi", Line("Groceries", 1170.00m, Rsd));
        var onlyThe3rd = new RateTable([new FxRate(Rsd, Oct(3), 117.00m)]);

        var summary = ForAllWallets(rows.Build(), Eur, onlyThe3rd);

        summary.Spent.Amount.Should().Be(10.00m);                // 1170.00 / 117.00, seven days old
        summary.AnyApproximate.Should().BeTrue();
        summary.OldestRateDate.Should().Be(Oct(3));
        summary.NewestRateDate.Should().Be(Oct(3));
        summary.Wallets.Should().Equal(new SummaryWalletLine(CashRsdId, "Cash RSD", Rsd, 1170.00m, 0m, false));
    }

    [Fact]
    public void With_RSD_missing_and_EUR_asked_the_EUR_wallets_still_count()
    {
        var rows = new SummaryRowsBuilder()
            .Wallet(WiseId, "Wise", Eur, Oct(1))
            .Wallet(CashRsdId, "Cash RSD", Rsd, Oct(1));
        rows.Expense(WiseId, Oct(7), "Café Central", Line("Cafés", 5.00m));
        rows.Expense(CashRsdId, Oct(3), "Maxi", Line("Groceries", 1170.00m, Rsd));
        var usdOnly = new RateTable(TestRates.Daily(Jul(1), Oct(31), (Usd, 1.10m)));

        var summary = ForAllWallets(rows.Build(), Eur, usdOnly);

        summary.NoRates.Should().BeFalse("the table holds rates, just none for RSD");
        summary.Spent.Amount.Should().Be(5.00m);
        Amounts(summary).Should().Equal(("Cafés", 5.00m));
        summary.NotConverted.Should().Equal(new SummaryNotConverted(Rsd, 1170.00m, 0m));
        summary.Wallets.Should().Equal(
            new SummaryWalletLine(CashRsdId, "Cash RSD", Rsd, 1170.00m, 0m, false),
            new SummaryWalletLine(WiseId, "Wise", Eur, 5.00m, 0m, false));
        summary.OldestRateDate.Should().BeNull();
        summary.NewestRateDate.Should().BeNull();
    }

    [Fact]
    public void A_wallet_compares_with_the_previous_month_and_the_average_from_its_history_start()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Aug(1));
        rows.Expense(WiseId, Aug(20), "Maxi", Line("Groceries", 40.00m));
        rows.Income(WiseId, Aug(25), "Employer", Line("Salary", 100.00m));
        rows.Expense(WiseId, Oct(3), "Maxi", Line("Groceries", 30.00m));

        var summary = ForWallet(rows.Build(), WiseId);

        summary.AverageMonths.Should().Be(2);                     // September and August; July is before the start
        summary.Spent.Should().Be(new SummaryAmount(30.00m, 0m, 20.00m));        // September is a real 0: (0 + 40.00) / 2
        summary.Received.Should().Be(new SummaryAmount(0m, 0m, 50.00m));         // (0 + 100.00) / 2
        summary.Net.Should().Be(new SummaryAmount(-30.00m, 0m, 30.00m));         // 0 - 30.00; 0 - 0; 50.00 - 20.00
        summary.Categories.Should().Equal(new SummaryCategory("Groceries", new SummaryAmount(30.00m, 0m, 20.00m)));
    }

    [Fact]
    public void With_no_history_before_the_month_there_is_no_average()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Oct(1));
        rows.Expense(WiseId, Oct(3), "Maxi", Line("Groceries", 30.00m));

        var summary = ForWallet(rows.Build(), WiseId);

        summary.AverageMonths.Should().Be(0);
        summary.Spent.Should().Be(new SummaryAmount(30.00m, null, null));
        summary.Net.Should().Be(new SummaryAmount(-30.00m, null, null));
    }

    [Fact]
    public void A_scopes_first_month_has_no_previous_month_to_compare_with()
    {
        var rows = new SummaryRowsBuilder()
            .Wallet(WiseId, "Wise", Eur, Oct(1))
            .Wallet(CashRsdId, "Cash RSD", Rsd, null);
        rows.Expense(WiseId, Oct(3), "Maxi", Line("Groceries", 30.00m));
        rows.Income(WiseId, Oct(5), "Employer", Line("Salary", 100.00m));
        rows.Transfer(Oct(6), CashRsdId, 1170.00m, WiseId, 10.00m);
        var built = rows.Build();

        // A-5: September is before Wise's history, so it is unknown, not a zero to read as "new" against (A-6).
        var wise = ForWallet(built, WiseId);
        wise.Spent.Should().Be(new SummaryAmount(30.00m, null, null));
        wise.Received.Should().Be(new SummaryAmount(100.00m, null, null));
        wise.Net.Should().Be(new SummaryAmount(70.00m, null, null));
        wise.Categories.Should().Equal(new SummaryCategory("Groceries", new SummaryAmount(30.00m, null, null)));

        // A wallet with no history start has nothing to compare against at all.
        ForWallet(built, CashRsdId).Spent.Should().Be(new SummaryAmount(0m, null, null));
    }

    [Fact]
    public void An_empty_previous_month_after_the_history_start_is_a_real_zero()
    {
        var throughThe8th = new SummaryPeriod(Oct(1), Oct(8), Finished: false);
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Sep(1));
        rows.Expense(WiseId, Sep(20), "Maxi", Line("Groceries", 40.00m));    // after 8 Sep: the history starts, 1–8 Sep is empty
        rows.Income(WiseId, Sep(25), "Employer", Line("Salary", 100.00m));
        rows.Expense(WiseId, Oct(3), "Maxi", Line("Groceries", 30.00m));
        rows.Income(WiseId, Oct(5), "Employer", Line("Salary", 50.00m));

        var summary = ForWallet(rows.Build(), WiseId, period: throughThe8th);

        summary.AverageMonths.Should().Be(1);
        summary.Spent.Should().Be(new SummaryAmount(30.00m, 0m, 0m));
        summary.Received.Should().Be(new SummaryAmount(50.00m, 0m, 0m));
        summary.Net.Should().Be(new SummaryAmount(20.00m, 0m, 0m));                 // 50.00 - 30.00; 0 - 0; 0 - 0
        summary.Categories.Should().Equal(new SummaryCategory("Groceries", new SummaryAmount(30.00m, 0m, 0m)));
    }

    [Fact]
    public void All_wallets_start_their_history_at_the_earliest_wallet_even_one_with_nothing_in_the_months_read()
    {
        var rows = new SummaryRowsBuilder()
            .Wallet(WiseId, "Wise", Eur, Sep(1))
            .Wallet(CashRsdId, "Cash RSD", Rsd, new DateOnly(2026, 1, 1));
        rows.Expense(WiseId, Sep(10), "Maxi", Line("Groceries", 20.00m));
        rows.Expense(WiseId, Oct(3), "Maxi", Line("Groceries", 30.00m));
        var built = rows.Build();

        var all = ForAllWallets(built, Eur);
        all.AverageMonths.Should().Be(3);                         // from January: July and August are real zeros
        all.Spent.Should().Be(new SummaryAmount(30.00m, 20.00m, 20.00m / 3m));   // (20.00 + 0 + 0) / 3

        var wise = ForWallet(built, WiseId);
        wise.AverageMonths.Should().Be(1);
        wise.Spent.Should().Be(new SummaryAmount(30.00m, 20.00m, 20.00m));
    }

    [Fact]
    public void The_current_month_compares_the_same_days_of_earlier_months()
    {
        var throughThe8th = new SummaryPeriod(Oct(1), Oct(8), Finished: false);
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Jul(1));
        rows.Expense(WiseId, Jul(31), "Maxi", Line("Groceries", 70.00m));   // after 8 Jul
        rows.Expense(WiseId, Aug(2), "Maxi", Line("Groceries", 6.00m));
        rows.Expense(WiseId, Sep(8), "Maxi", Line("Groceries", 10.00m));
        rows.Expense(WiseId, Sep(9), "Maxi", Line("Groceries", 99.00m));    // after 8 Sep
        rows.Expense(WiseId, Oct(8), "Maxi", Line("Groceries", 20.00m));
        rows.Expense(WiseId, Oct(9), "Maxi", Line("Groceries", 500.00m));   // dated after today

        var summary = ForWallet(rows.Build(), WiseId, period: throughThe8th);

        summary.Period.Should().Be(throughThe8th);
        summary.Spent.Should().Be(new SummaryAmount(20.00m, 10.00m, 16.00m / 3m));   // 1–8 Sep, Aug, Jul: (10.00 + 6.00 + 0) / 3
    }

    [Fact]
    public void Categories_are_the_months_own_with_their_comparisons()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Jul(1));
        rows.Expense(WiseId, Sep(12), "Café Central", Line("Cafés", 9.00m));
        rows.Expense(WiseId, Oct(3), "Maxi", Line("Groceries", 30.00m));

        var summary = ForWallet(rows.Build(), WiseId);

        summary.Categories.Should().Equal(new SummaryCategory("Groceries", new SummaryAmount(30.00m, 0m, 0m)));
    }

    [Fact]
    public void Wallet_options_are_the_wallets_with_anything_in_the_months_read_and_the_selected_one()
    {
        var rows = new SummaryRowsBuilder()
            .Wallet(WiseId, "Wise", Eur, Oct(1))
            .Wallet(CashRsdId, "Cash RSD", Rsd, null)
            .Wallet(OldRevolutId, "Old Revolut", Eur, Sep(1), archived: true)
            .Wallet(MainId, "Main Wallet", Eur, null)
            .Wallet(DormantId, "Dormant", Eur, new DateOnly(2025, 1, 1), archived: true);
        rows.Expense(WiseId, Oct(3), "Maxi", Line("Groceries", 30.00m));
        rows.Transfer(Aug(5), CashRsdId, 1170.00m, WiseId, 10.00m);
        rows.Expense(OldRevolutId, Sep(4), "Zara", Line("Clothes", 25.00m));
        var built = rows.Build();

        var all = ForAllWallets(built, Eur);
        all.WalletOptions.Select(option => option.WalletName)
            .Should().Equal("Cash RSD", "Wise", "Old Revolut");   // active first, then by name; Main and Dormant are idle
        ForWallet(built, WiseId).WalletOptions.Should().Equal(all.WalletOptions);   // the same in every scope

        // A real wallet with nothing in the months read is a valid scope: zeros, and it joins the options.
        var main = ForWallet(built, MainId);
        main.WalletOptions.Select(option => option.WalletName).Should().Equal("Cash RSD", "Main Wallet", "Wise", "Old Revolut");
        main.WalletOptions.Should().Contain(new SummaryWalletOption(OldRevolutId, "Old Revolut", Eur, true));
        main.Spent.Amount.Should().Be(0m);
        main.HasAnyRecords.Should().BeFalse();
    }

    [Fact]
    public void A_month_with_nothing_counted_has_no_records_but_a_wallets_move_is_something()
    {
        var rows = new SummaryRowsBuilder()
            .Wallet(WiseId, "Wise", Eur, Sep(1))
            .Wallet(CashRsdId, "Cash RSD", Rsd, null);
        rows.Expense(WiseId, Sep(10), "Maxi", Line("Groceries", 20.00m));
        rows.Transfer(Oct(5), CashRsdId, 1170.00m, WiseId, 10.00m);
        var built = rows.Build();

        ForAllWallets(built, Eur).HasAnyRecords.Should().BeFalse("movements are not counted across wallets");
        ForWallet(built, CashRsdId).HasAnyRecords.Should().BeTrue();
    }

    [Fact]
    public void A_line_of_an_unknown_kind_is_refused()
    {
        var rows = new SummaryRows(
            [new SummaryWallet(WiseId, "Wise", Eur, false, Oct(1))],
            [new SummaryLineRow(Guid.NewGuid(), Oct(2), WiseId, Eur, SummaryLineKind.Unknown, EntryRole.Principal, 0,
                "Groceries", null, "Bread", 3.00m, Eur, null)],
            []);

        var act = () => ForWallet(rows, WiseId);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Highlights_are_the_three_largest_changes_against_the_average_ties_by_name()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Jul(1));
        foreach (var first in new[] { Jul(1), Aug(1), Sep(1) })
        {
            rows.Expense(WiseId, first.AddDays(2), "Maxi", Line("Groceries", 140.10m));
            rows.Expense(WiseId, first.AddDays(3), "Café Central", Line("Cafés", 135.00m));
            rows.Expense(WiseId, first.AddDays(4), "Landlord", Line("Rent", 500.00m));
        }

        rows.Expense(WiseId, Jul(10), "Bolt", Line("Transport", 60.00m));
        rows.Expense(WiseId, Aug(10), "Bookstore", Line("Books", 27.00m));
        rows.Expense(WiseId, Oct(3), "Maxi", Line("Groceries", 180.10m));
        rows.Expense(WiseId, Oct(4), "Café Central", Line("Cafés", 95.00m));
        rows.Expense(WiseId, Oct(5), "Landlord", Line("Rent", 500.00m));
        rows.Expense(WiseId, Oct(6), "Bolt", Line("Transport", 50.00m));
        rows.Expense(WiseId, Oct(7), "Bookstore", Line("Books", 10.00m));

        var summary = ForWallet(rows.Build(), WiseId);

        // Averages over Jul–Sep: Groceries 420.30 / 3 = 140.10, Cafés 405.00 / 3 = 135.00, Transport 60.00 / 3 = 20.00,
        // Books 27.00 / 3 = 9.00, Rent 500.00. Changes: +40.00, -40.00, +30.00, +1.00, 0. Cafés and Groceries tie at
        // 40.00 and go by name; Books is fourth; an unchanged Rent says nothing.
        summary.Highlights.Should().Equal(
            new SummaryHighlight("Cafés", 95.00m, -40.00m, HighlightBase.Average),
            new SummaryHighlight("Groceries", 180.10m, 40.00m, HighlightBase.Average),
            new SummaryHighlight("Transport", 50.00m, 30.00m, HighlightBase.Average));
    }

    [Fact]
    public void A_scope_whose_history_starts_this_month_has_nothing_to_highlight()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Oct(1));
        rows.Expense(WiseId, Oct(3), "Maxi", Line("Groceries", 180.10m));
        rows.Expense(WiseId, Oct(4), "Café Central", Line("Cafés", 95.00m));

        var summary = ForWallet(rows.Build(), WiseId);

        // A-5: September is before Wise's history - unknown, not a zero - so there is neither an average nor a previous
        // month to compare with, and reading it as 0 would call all of October a change.
        summary.Highlights.Should().BeEmpty();
    }

    [Fact]
    public void Top_merchants_rank_by_spent_with_their_record_counts()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Oct(1));
        rows.Expense(WiseId, Oct(2), "Maxi", Line("Groceries", 10.00m));
        rows.Expense(WiseId, Oct(3), "Maxi", Line("Groceries", 20.00m));
        rows.Expense(WiseId, Oct(4), "Maxi", Line("Groceries", 30.00m));
        rows.Expense(WiseId, Oct(5), "Lidl", Line("Groceries", 70.00m));
        rows.Expense(WiseId, Oct(6), "Gigatron", Line("Electronics", 89.00m));
        rows.Expense(WiseId, Oct(7), "IKEA", Line("Home", 15.00m));
        rows.Expense(WiseId, Oct(8), "Zara", Line("Clothes", 25.00m));
        rows.Expense(WiseId, Oct(9), "Bolt", Line("Transport", 5.00m));
        rows.Expense(WiseId, Oct(10), null, Line("Rent", 100.00m, description: "Rent cash"));

        var summary = ForWallet(rows.Build(), WiseId);

        // Maxi: 10.00 + 20.00 + 30.00 over three records; Bolt is sixth; a record with no merchant is not a merchant.
        summary.TopMerchants.Should().Equal(
            new SummaryMerchant("Gigatron", 89.00m, 1),
            new SummaryMerchant("Lidl", 70.00m, 1),
            new SummaryMerchant("Maxi", 60.00m, 3),
            new SummaryMerchant("Zara", 25.00m, 1),
            new SummaryMerchant("IKEA", 15.00m, 1));
    }

    [Fact]
    public void Largest_records_count_a_charge_with_its_fee_and_a_transfer_fee_is_a_record()
    {
        var rows = new SummaryRowsBuilder()
            .Wallet(WiseId, "Wise", Eur, Oct(1))
            .Wallet(CashRsdId, "Cash RSD", Rsd, null)
            .Wallet(KaspiId, "Kaspi", Kzt, Oct(1));
        var paddle = rows.ChargedExpense(KaspiId, Oct(4), "Paddle", charged: 15601.00m,
            Line("Subscriptions", 30.00m, Usd, "SaaS"), Fee(156.00m, Kzt));
        var maxi = rows.Expense(WiseId, Oct(2), "Maxi", Line("Groceries", 25.00m));
        var kiosk = rows.Transfer(Oct(5), WiseId, 101.00m, CashRsdId, 11700.00m, TransferLeg.From, 1.00m, "Exchange Kiosk");
        var bank = rows.Transfer(Oct(6), CashRsdId, 5850.00m, WiseId, 49.50m, TransferLeg.To, 0.50m);
        rows.Refund(WiseId, Oct(9), "Maxi", Line("Groceries", 45.00m));
        rows.Income(WiseId, Oct(1), "Employer", Line("Salary", 2000.00m));

        var summary = ForAllWallets(rows.Build(), Eur);

        // Paddle: (15601.00 charge + 156.00 fee) KZT / 520.00 = 30.3019 EUR; the refund record is negative, the salary
        // spends nothing, the kiosk's fee is labelled by its venue and the bank transfer's by "Transfer fee".
        summary.LargestRecords.Select(record => (record.TransactionId, record.Label))
            .Should().Equal((paddle, "Paddle"), (maxi, "Maxi"), (kiosk, "Exchange Kiosk"), (bank, "Transfer fee"));
        summary.LargestRecords[0].Amount.Should().BeApproximately(30.3019m, 0.0001m);
        summary.LargestRecords[0].OccurredOn.Should().Be(Oct(4));
        summary.LargestRecords.Skip(1).Select(record => record.Amount).Should().Equal(25.00m, 1.00m, 0.50m);
        summary.LargestRecords.Should().OnlyContain(record => !record.Approximate);

        // Maxi's refund outweighs its purchase (25.00 - 45.00 = -20.00), so Maxi is no top merchant; the kiosk's fee is.
        summary.TopMerchants.Select(merchant => (merchant.MerchantName, merchant.Records))
            .Should().Equal(("Paddle", 1), ("Exchange Kiosk", 1));
    }

    [Fact]
    public void A_record_with_no_merchant_is_labelled_by_its_first_lines_description()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Oct(1));
        var taxi = rows.Expense(WiseId, Oct(3), null,
            Line("Transport", 20.00m, description: "Taxi to airport"),
            Line("Transport", 2.00m, description: "Tip"));
        var built = rows.Build();
        var reversed = built with { Lines = [.. built.Lines.Reverse()] };

        var summary = ForWallet(reversed, WiseId);

        summary.LargestRecords.Should().Equal(new SummaryRecord(taxi, Oct(3), "Taxi to airport", 22.00m, false));
    }

    [Fact]
    public void Largest_records_are_five_by_amount_then_day_then_label()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Oct(1));
        rows.Expense(WiseId, Oct(9), "Alpha", Line("Groceries", 50.00m));
        rows.Expense(WiseId, Oct(3), "Delta", Line("Groceries", 40.00m));
        rows.Expense(WiseId, Oct(2), "Charlie", Line("Groceries", 40.00m));
        rows.Expense(WiseId, Oct(2), "Bravo", Line("Groceries", 40.00m));
        rows.Expense(WiseId, Oct(5), "Echo", Line("Groceries", 30.00m));
        rows.Expense(WiseId, Oct(6), "Foxtrot", Line("Groceries", 20.00m));

        var summary = ForWallet(rows.Build(), WiseId);

        summary.LargestRecords.Select(record => record.Label).Should().Equal("Alpha", "Bravo", "Charlie", "Delta", "Echo");
    }

    [Fact]
    public void A_converted_record_is_marked_approximate()
    {
        var rows = new SummaryRowsBuilder().Wallet(WiseId, "Wise", Eur, Oct(1));
        var jetBrains = rows.Expense(WiseId, Oct(10), "JetBrains", Line("Software", 11.00m, Usd));

        var summary = ForWallet(rows.Build(), WiseId);

        summary.LargestRecords.Should().Equal(new SummaryRecord(jetBrains, Oct(10), "JetBrains", 10.00m, true));
        summary.TopMerchants.Should().Equal(new SummaryMerchant("JetBrains", 10.00m, 1));
    }
}
