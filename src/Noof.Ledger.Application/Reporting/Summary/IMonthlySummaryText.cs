using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting.Summary;

public interface IMonthlySummaryText
{
    // The one plain-text rendering (bot, automatic summary, both Explain requests), English, at most 3 800 characters.
    string Render(MonthlySummary summary);

    // One sentence, e.g. "Groceries: 180.10 EUR, 40.00 more than your 3-month average."
    string Highlight(SummaryHighlight highlight, CurrencyCode currency, int averageMonths);

    // "+12%", "-5%", "new", or null (nothing to compare against) — spec A-6; never for net.
    string? Percent(decimal amount, decimal? baseline);
}
