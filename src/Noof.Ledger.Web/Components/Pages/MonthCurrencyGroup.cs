using MudBlazor;
using Noof.Ledger.Application.Reporting;

namespace Noof.Ledger.Web.Components.Pages;

internal sealed record MonthCurrencyGroup(string Currency, IReadOnlyList<MonthTotal> Totals, IReadOnlyList<MonthTotal> Received)
{
    public decimal Total => Totals.Sum(total => total.Amount);

    public decimal ReceivedTotal => Received.Sum(total => total.Amount);

    // A refund above its category's purchases leaves the category at or below zero (8b spec SS-19, P-6), and a donut
    // has no negative slice: the chart draws only what was spent, while the table and the Spent total keep every category.
    public IReadOnlyList<MonthTotal> Slices => [.. Totals.Where(total => total.Amount > 0)];

    public List<ChartSeries<double>> Series =>
        [new() { Name = Currency, Data = new ChartData<double>([.. Slices.Select(total => (double)total.Amount)]) }];

    public string[] Labels => [.. Slices.Select(total => total.CategoryName)];
}
