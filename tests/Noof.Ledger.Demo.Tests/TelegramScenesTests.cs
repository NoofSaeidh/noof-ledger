using AwesomeAssertions;
using Noof.Ledger.Demo.Shots;

namespace Noof.Ledger.Demo.Tests;

public sealed class TelegramScenesTests
{
    static readonly IReadOnlyList<ChatScene> Scenes = TelegramScenes.Build(TelegramScenes.CreateEcho());

    [Fact]
    public void Every_scene_the_spec_lists_is_built()
    {
        Scenes.Select(scene => scene.Name).Should().Equal(
            "expense", "multi-line", "income", "balance", "cancel-restore", "correction", "failure",
            "transfer", "transfer-fee", "exchange", "foreign-spending", "no-terms", "exchange-question",
            "receipt-qr", "receipt-vision", "receipt-check", "slip", "slip-check", "slip-incomplete", "slip-unsupported-currency",
            "health", "bug");
    }

    [Theory]
    [InlineData("expense", "Recorded — Raiffeisen", new[] { "Cancel", "Edit" })]
    [InlineData("income", "Income — Wise", new[] { "Cancel", "Edit" })]
    [InlineData("cancel-restore", "Cancelled — Raiffeisen", new[] { "Restore" })]
    [InlineData("failure", "Could not read that message.", new[] { "Edit" })]
    [InlineData("transfer", "Transfer — 10000.00 RSD · Raiffeisen → Cash RSD", new[] { "Cancel", "Edit" })]
    [InlineData("transfer-fee", "Transfer — Raiffeisen -10150.00 RSD (incl. fee 150.00) → Cash RSD +10000.00 RSD", new[] { "Cancel", "Edit" })]
    [InlineData("exchange", "Exchange — 100.00 EUR (Cash EUR) → 11700.00 RSD (Cash RSD) · 1 EUR = 117.0000 RSD", new[] { "Cancel", "Edit" })]
    [InlineData("foreign-spending", "Recorded — Kaspi · balance 169744.00 KZT", new[] { "Cancel", "Edit" })]
    [InlineData("no-terms", "Recorded — Kaspi · balance 185500.00 KZT, -20.00 EUR", new[] { "Cancel", "Edit" })]
    [InlineData("exchange-question", "Exchange not recorded: how much did you get?", new[] { "Edit" })]
    [InlineData("receipt-qr", "Recorded — Maxi — Dorćol · Raiffeisen", new[] { "Cancel", "Edit" })]
    [InlineData("receipt-vision", "Recorded — Apoteka Zdravlje · Raiffeisen", new[] { "Cancel", "Edit" })]
    [InlineData("receipt-check", "This receipt doesn't look right — Pekara Centar", new[] { "Record anyway", "Cancel" })]
    [InlineData("slip", "Exchange — 100.00 EUR (Cash EUR) → 11700.00 RSD (Cash RSD)", new[] { "Cancel", "Edit" })]
    [InlineData("slip-check", "This exchange slip doesn't look right — Menjačnica Zlatnik", new[] { "Record anyway", "Cancel" })]
    [InlineData("slip-incomplete", "Slip read, but", new[] { "Edit" })]
    [InlineData("slip-unsupported-currency", "Slip read, but CHF", new[] { "Edit" })]
    public void The_bot_bubble_carries_the_apps_own_text_and_buttons(string scene, string start, string[] buttons)
    {
        var reply = Scenes.Single(candidate => candidate.Name == scene).Bubbles.Last(bubble => bubble.Side == ChatSide.Bot);

        reply.Text.Should().StartWith(start);
        reply.Buttons.Should().Equal(buttons);
    }

    [Fact]
    public void The_multi_line_message_names_its_merchant_and_the_balance_statement_says_it_adjusted()
    {
        Scenes.Single(scene => scene.Name == "multi-line").Bubbles[^1].Text.Should().Contain("Lidl");
        Scenes.Single(scene => scene.Name == "balance").Bubbles[^1].Text.Should().Contain("adjusted");
    }

    [Theory]
    [InlineData("receipt-qr", "902")]
    [InlineData("receipt-vision", "904")]
    [InlineData("receipt-check", "903")]
    public void A_receipt_picture_starts_from_a_photo_and_lists_the_demo_databases_own_receipt_lines(string scene, string id)
    {
        var bubbles = Scenes.Single(candidate => candidate.Name == scene).Bubbles;
        var receipt = MockData.Records.Single(record => record.Id == MockData.Id(int.Parse(id))).Receipt!;

        bubbles[0].Photo.Should().BeTrue();
        foreach (var line in receipt.Lines)
            bubbles[^1].Text.Should().Contain(line.Name);
    }

    [Fact]
    public void A_slip_picture_starts_from_a_photo_and_names_its_office_and_where_it_was_read()
    {
        var bubbles = Scenes.Single(scene => scene.Name == "slip").Bubbles;

        bubbles[0].Photo.Should().BeTrue();
        bubbles[^1].Text.Should().EndWith("\nMenjačnica Zlatnik · from a slip photo\n⚠️ Read from the slip photo — check the figures.");
    }

    [Fact]
    public void The_held_slip_picture_is_the_demo_databases_own_held_slip()
    {
        var bubbles = Scenes.Single(scene => scene.Name == "slip-check").Bubbles;
        var slip = MockData.Records.Single(record => record.Id == MockData.HeldSlipTransactionId).Receipt!;

        bubbles[0].Photo.Should().BeTrue();
        bubbles[^1].Text.Should().Contain($"\nPIB: {slip.SellerTaxId}\nSlip #: {slip.Exchange!.SlipNumber}\n")
            .And.Contain(FormattableString.Invariant($"\nReceived: {slip.Exchange.ReceivedAmount:0.00} {slip.Exchange.ReceivedCurrency}\n"));
    }

    [Fact]
    public void An_incomplete_slip_picture_starts_from_a_photo_and_asks_only_for_the_unread_figure()
    {
        var bubbles = Scenes.Single(scene => scene.Name == "slip-incomplete").Bubbles;

        bubbles[0].Photo.Should().BeTrue();
        bubbles[^1].Text.Should().Be("Slip read, but the amount received is unreadable — reply with it.");
    }

    [Fact]
    public void A_slip_in_a_currency_the_ledger_lacks_names_the_currency_instead_of_asking_for_it()
    {
        var bubbles = Scenes.Single(scene => scene.Name == "slip-unsupported-currency").Bubbles;

        bubbles[0].Photo.Should().BeTrue();
        bubbles[^1].Text.Should().Be(
            "Slip read, but CHF isn't a currency this ledger holds — nothing recorded. If it was misread, reply with the right currency.");
    }

    [Fact]
    public void The_vision_receipt_says_the_tax_administration_was_unavailable()
    {
        Scenes.Single(scene => scene.Name == "receipt-vision").Bubbles[^1].Text.Should().Contain("Tax Administration unavailable");
    }

    [Fact]
    public void The_health_reply_is_the_bots_own_format()
    {
        Scenes.Single(scene => scene.Name == "health").Bubbles[^1].Text.Should().StartWith("Health: all good");
    }

    [Fact]
    public void The_transfer_and_foreign_currency_pictures_carry_the_fee_the_charge_and_the_rate_hint()
    {
        Scenes.Single(scene => scene.Name == "transfer-fee").Bubbles[^1].Text.Should().Contain("\nFee 150.00 RSD · Fees & Charges\n");
        Scenes.Single(scene => scene.Name == "foreign-spending").Bubbles[^1].Text.Should().EndWith(
            "\n30.00 USD → charged 15600.00 KZT (1 USD = 520.0000 KZT, wallet rate) + fee 156.00 KZT");
        Scenes.Single(scene => scene.Name == "no-terms").Bubbles[^1].Text.Should().EndWith(
            "\n20.00 EUR not converted — set a EUR rate for Kaspi on /wallets, or correct this record to apply it");
    }

    [Fact]
    public void The_bug_scene_saves_the_report_then_answers_with_the_explanation_and_the_records_findings()
    {
        var bubbles = Scenes.Single(scene => scene.Name == "bug").Bubbles;

        bubbles[0].Side.Should().Be(ChatSide.Operator);
        bubbles[0].Text.Should().Be("/bug the amount is wrong");
        bubbles[0].Quote.Should().StartWith("Exchange not recorded");
        bubbles[1].Text.Should().Be("Bug report #4 saved.");
        bubbles[1].Quote.Should().Be("/bug the amount is wrong");
    }
}
