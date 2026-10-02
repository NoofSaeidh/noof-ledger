using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class SlipRequestTextTests
{
    static string FiguresOf(ExtractedExchange evidence) =>
        string.Join('\n', SlipRequestText.For(new SlipFacts(null, null, evidence), note: null).Split('\n')[2..]);

    // A commission taken from what the customer got back: the received side as a person would say it is what was paid
    // out (spec A-21), so "11550 after a fee of 150 already taken out" settles to the 11550 actually received.
    [Fact]
    public void A_commission_on_the_received_side_is_shown_already_taken_out_of_what_arrived()
    {
        var evidence = new ExtractedExchange(100.0000m, "EUR", 11550.0000m, "RSD", 117.000000000000m, 150.0000m, null, null);

        FiguresOf(evidence).Should().Be(
            "Office: not read\n"
            + "Given: 100.00 EUR\n"
            + "Received: 11550.00 RSD, after a fee of 150.00 RSD on this side (already taken out of the figure)\n"
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
            + "Given: (amount not read) RSD\n"
            + "Received: 100.00 EUR\n"
            + "Rate printed on the slip (dinars per one unit of the foreign currency): not read\n"
            + "Commission: 150.00 RSD (printed inside the amount on its side)");
    }

    // A side's currency is read on its own: an unread amount must not take the currency with it, or a reply that says
    // only "received 100" would leave the model guessing which currency arrived.
    [Fact]
    public void A_side_whose_amount_was_not_read_still_names_its_currency()
    {
        var evidence = new ExtractedExchange(null, "usd", null, "RSD", null, null, null, null);

        FiguresOf(evidence).Should().StartWith(
            "Office: not read\n"
            + "Given: (amount not read) USD\n"
            + "Received: (amount not read) RSD\n");
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
