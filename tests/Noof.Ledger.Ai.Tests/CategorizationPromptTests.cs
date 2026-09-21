using System.Text.RegularExpressions;
using AwesomeAssertions;
using Noof.Ledger.Ai;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Tests;

public class CategorizationPromptTests
{
    [Fact]
    public void System_prompt_instructs_verbatim_amount_quoting()
    {
        CategorizationPrompt.System.Should().Contain("character for character");
        CategorizationPrompt.System.Should().Contain("never compute, round or sum anything");
        CategorizationPrompt.System.Should().Contain("do not produce that line");
    }

    [Fact]
    public void System_prompt_wraps_examples_in_the_examples_tag()
    {
        CategorizationPrompt.System.Should().Contain("<examples>");
        CategorizationPrompt.System.Should().Contain("</examples>");
    }

    [Fact]
    public void System_prompt_has_between_three_and_five_examples()
    {
        var opening = Regex.Matches(CategorizationPrompt.System, "<example>").Count;
        var closing = Regex.Matches(CategorizationPrompt.System, "</example>").Count;

        opening.Should().BeInRange(3, 5);
        closing.Should().Be(opening);
    }

    [Fact]
    public void System_prompt_contains_no_secret_looking_text()
    {
        CategorizationPrompt.System.Should().NotContain("sk-ant");
        CategorizationPrompt.System.Should().NotMatchRegex("(?i)api[_-]?key");
        CategorizationPrompt.System.Should().NotMatchRegex("[A-Za-z0-9_-]{32,}");
    }

    [Fact]
    public void Render_categories_shows_both_names_and_the_parent_when_present()
    {
        IReadOnlyList<CategoryOption> categories =
        [
            new CategoryOption("groceries", "Groceries", "Продукты", null),
            new CategoryOption("dairy", "Dairy", "Молочные продукты", "groceries"),
        ];

        var rendered = CategorizationPrompt.RenderCategories(categories);

        rendered.Should().Contain("groceries: Groceries / Продукты");
        rendered.Should().Contain("dairy (under groceries): Dairy / Молочные продукты");
    }

    [Fact]
    public void Render_merchant_hints_lists_id_and_display_name()
    {
        IReadOnlyList<MerchantOption> hints =
            [new MerchantOption(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Lidl")];

        CategorizationPrompt.RenderMerchantHints(hints)
            .Should().Contain("11111111-1111-1111-1111-111111111111: Lidl");
    }

    [Fact]
    public void Render_merchant_hints_says_so_when_there_are_none()
    {
        CategorizationPrompt.RenderMerchantHints([]).Should().Contain("No known merchants");
    }

    [Fact]
    public void Build_user_turn_includes_the_raw_text_the_categories_and_the_hints()
    {
        IReadOnlyList<CategoryOption> categories = [new CategoryOption("groceries", "Groceries", "Продукты", null)];
        IReadOnlyList<MerchantOption> hints = [];

        var turn = CategorizationPrompt.BuildUserTurn("кофе 250 рсд", categories, hints);

        turn.Should().Contain("кофе 250 рсд");
        turn.Should().Contain("groceries: Groceries / Продукты");
        turn.Should().Contain("No known merchants");
    }
}
