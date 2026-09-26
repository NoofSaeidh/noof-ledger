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
            "expense", "receipt", "income", "balance", "cancel-restore", "correction", "failure", "health");
    }

    [Theory]
    [InlineData("expense", "Recorded — Raiffeisen", new[] { "Cancel", "Edit" })]
    [InlineData("income", "Income — Wise", new[] { "Cancel", "Edit" })]
    [InlineData("cancel-restore", "Cancelled — Raiffeisen", new[] { "Restore" })]
    [InlineData("failure", "Could not read that message.", new[] { "Edit" })]
    public void The_bot_bubble_carries_the_apps_own_text_and_buttons(string scene, string start, string[] buttons)
    {
        var reply = Scenes.Single(candidate => candidate.Name == scene).Bubbles.Last(bubble => bubble.Side == ChatSide.Bot);

        reply.Text.Should().StartWith(start);
        reply.Buttons.Should().Equal(buttons);
    }

    [Fact]
    public void The_receipt_names_its_merchant_and_the_balance_statement_says_it_adjusted()
    {
        Scenes.Single(scene => scene.Name == "receipt").Bubbles[^1].Text.Should().Contain("Lidl");
        Scenes.Single(scene => scene.Name == "balance").Bubbles[^1].Text.Should().Contain("adjusted");
    }

    [Fact]
    public void The_health_reply_is_the_bots_own_format()
    {
        Scenes.Single(scene => scene.Name == "health").Bubbles[^1].Text.Should().StartWith("Health: all good");
    }
}
