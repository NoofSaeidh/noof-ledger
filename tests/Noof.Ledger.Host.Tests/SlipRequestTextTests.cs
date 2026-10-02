using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class SlipRequestTextTests
{
    static string FiguresOf(ExtractedExchange evidence) =>
        string.Join('\n', SlipRequestText.For(new SlipFacts(null, null, evidence), note: null).Split('\n')[2..]);

    // A commission taken from what the customer got back: the received side as a person would say it is the amount
    // before the fee came off, so "11700 plus a fee of 150 not included" settles to the 11550 actually received.
    [Fact]
    public void A_commission_on_the_received_side_is_added_back_and_shown_beside_it()
    {
        var evidence = new ExtractedExchange(100.0000m, "EUR", 11550.0000m, "RSD", 117.000000000000m, 150.0000m, null, null);

        FiguresOf(evidence).Should().Be(
            "Office: not read\n"
            + "Given: 100.00 EUR\n"
            + "Received: 11700.00 RSD, plus a fee of 150.00 RSD on this side (not included in the figure)\n"
            + "Rate printed on the slip (dinars per one unit of the foreign currency): 117.0000");
    }

    // With its side's amount unread there is nothing to take the commission out of: the reply will say that side as
    // it was handed over, commission inside, so the line says so.
    [Fact]
    public void A_commission_whose_side_was_not_read_stands_on_its_own_line()
    {
        var evidence = new ExtractedExchange(null, "RSD", 100.0000m, "EUR", null, 150.0000m, "rsd", null);

        FiguresOf(evidence).Should().Be(
            "Office: not read\n"
            + "Given: not read\n"
            + "Received: 100.00 EUR\n"
            + "Rate printed on the slip (dinars per one unit of the foreign currency): not read\n"
            + "Commission: 150.00 RSD (printed inside the amount on its side)");
    }

    [Fact]
    public void A_commission_in_neither_sides_currency_stands_on_its_own_line()
    {
        var evidence = new ExtractedExchange(100.0000m, "EUR", 11700.0000m, "RSD", null, 1.0000m, "USD", null);

        FiguresOf(evidence).Should().EndWith("\nCommission: 1.00 USD (printed inside the amount on its side)");
    }

    // "0.00" alone would round a fourth decimal away, and the rate keeps every significant decimal it was stored with.
    [Fact]
    public void Figures_keep_every_significant_decimal_the_store_holds()
    {
        var evidence = new ExtractedExchange(100.1250m, "eur", null, null, 117.123456789012m, null, null, null);

        FiguresOf(evidence).Should().Be(
            "Office: not read\n"
            + "Given: 100.125 EUR\n"
            + "Received: not read\n"
            + "Rate printed on the slip (dinars per one unit of the foreign currency): 117.123456789012\n"
            + "Commission: not read");
    }
}
