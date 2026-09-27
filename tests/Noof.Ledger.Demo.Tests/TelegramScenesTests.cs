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
            "receipt-qr", "receipt-vision", "receipt-check", "health");
    }

    [Theory]
    [InlineData("expense", "Recorded — Raiffeisen", new[] { "Cancel", "Edit" })]
    [InlineData("income", "Income — Wise", new[] { "Cancel", "Edit" })]
    [InlineData("cancel-restore", "Cancelled — Raiffeisen", new[] { "Restore" })]
    [InlineData("failure", "Could not read that message.", new[] { "Edit" })]
    [InlineData("receipt-qr", "Recorded — Maxi — Dorćol · Raiffeisen", new[] { "Cancel", "Edit" })]
    [InlineData("receipt-vision", "Recorded — Apoteka Zdravlje · Raiffeisen", new[] { "Cancel", "Edit" })]
    [InlineData("receipt-check", "This receipt doesn't look right — Pekara Centar", new[] { "Record anyway", "Cancel" })]
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
    public void The_vision_receipt_says_the_tax_administration_was_unavailable()
    {
        Scenes.Single(scene => scene.Name == "receipt-vision").Bubbles[^1].Text.Should().Contain("Tax Administration unavailable");
    }

    [Fact]
    public void The_health_reply_is_the_bots_own_format()
    {
        Scenes.Single(scene => scene.Name == "health").Bubbles[^1].Text.Should().StartWith("Health: all good");
    }
}
