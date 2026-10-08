using AwesomeAssertions;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;
using Noof.Ledger.Web.Components.Pages;

namespace Noof.Ledger.Host.Tests;

public class MonthCurrencyGroupTests
{
    [Fact]
    public void A_category_at_or_below_zero_stays_in_the_table_and_the_total_but_not_in_the_donut()
    {
        var group = new MonthCurrencyGroup("RSD",
        [
            new MonthTotal("Groceries", CurrencyCode.Rsd, 1200.00m),
            new MonthTotal("Transport", CurrencyCode.Rsd, 850.00m),
            new MonthTotal("Pharmacy", CurrencyCode.Rsd, 0.00m),
            new MonthTotal("Clothes", CurrencyCode.Rsd, -300.00m),
        ], []);

        group.Labels.Should().Equal("Groceries", "Transport");
        group.Slices.Select(total => total.Amount).Should().Equal(1200.00m, 850.00m);
        group.Totals.Should().HaveCount(4, "the table under the donut keeps every category, a refunded one included");
        group.Total.Should().Be(1750.00m, "the Spent figure is the sum of every category: 1200 + 850 + 0 - 300");
    }

    [Fact]
    public void One_category_above_zero_beside_a_refunded_one_leaves_a_single_slice()
    {
        var group = new MonthCurrencyGroup("RSD",
        [
            new MonthTotal("Groceries", CurrencyCode.Rsd, 500.00m),
            new MonthTotal("Clothes", CurrencyCode.Rsd, -300.00m),
        ], []);

        group.Slices.Should().ContainSingle("a single slice is no distribution, so Home draws no donut for it");
    }
}
