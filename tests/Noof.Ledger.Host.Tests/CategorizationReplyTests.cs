using AwesomeAssertions;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class CategorizationReplyTests
{
    [Fact]
    public void Composes_the_success_text_for_a_single_item()
    {
        var lines = new[] { new CategorizationReply.ReplyLine("Coffee", new Money(3.50m, CurrencyCode.Eur), "Food & drink") };

        var text = CategorizationReply.ComposeSuccess("Cash", lines);

        text.Should().Be("Categorised — Cash\n• Coffee — 3.50 EUR (Food & drink)\n\nTotal: 3.50 EUR");
    }

    [Fact]
    public void Composes_the_success_text_across_two_currencies_with_totals_grouped_and_sorted()
    {
        var lines = new[]
        {
            new CategorizationReply.ReplyLine("Groceries", new Money(25m, CurrencyCode.Eur), "Groceries"),
            new CategorizationReply.ReplyLine("Taxi", new Money(1500m, CurrencyCode.Rsd), "Transport"),
        };

        var text = CategorizationReply.ComposeSuccess("Cash", lines);

        text.Should().Be(
            "Categorised — Cash\n" +
            "• Groceries — 25.00 EUR (Groceries)\n" +
            "• Taxi — 1500.00 RSD (Transport)\n\n" +
            "Total: 25.00 EUR, 1500.00 RSD");
    }

    [Fact]
    public void Sums_more_than_one_line_in_the_same_currency_into_one_total()
    {
        var lines = new[]
        {
            new CategorizationReply.ReplyLine("Bread", new Money(2.50m, CurrencyCode.Eur), "Groceries"),
            new CategorizationReply.ReplyLine("Milk", new Money(1.20m, CurrencyCode.Eur), "Groceries"),
        };

        var text = CategorizationReply.ComposeSuccess("Cash", lines);

        text.Should().EndWith("Total: 3.70 EUR");
    }

    [Fact]
    public void Composes_the_failure_text()
    {
        var text = CategorizationReply.ComposeFailure("Cash");

        text.Should().Be(
            "I couldn't categorise this one for Cash automatically. It's saved, nothing is lost, " +
            "but you'll need to sort it out by hand for now.");
    }

    [Fact]
    public void Composes_the_nothing_to_record_text_distinctly_from_success_and_failure()
    {
        // A loan received ("заняла у Маши 5000 рсд") resolves with zero line items - a real,
        // positive read of the message, not a failure. The operator must be able to tell this
        // apart from ComposeFailure's "I couldn't categorise this" at a glance.
        var text = CategorizationReply.ComposeNothingToRecord("Cash");

        text.Should().Be("Read this for Cash — nothing here looks like spending, so nothing was recorded.");
        text.Should().NotContain("couldn't categorise");
    }
}
