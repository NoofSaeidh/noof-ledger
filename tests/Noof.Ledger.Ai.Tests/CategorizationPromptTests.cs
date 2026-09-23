using System.Text.RegularExpressions;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Tests;

public class CategorizationPromptTests
{
    [Fact]
    public void System_prompt_asks_for_the_meant_amount_as_a_plain_decimal()
    {
        CategorizationPrompt.System.Should().Contain("the number the person meant");
        CategorizationPrompt.System.Should().Contain("\"штуку\"");
        CategorizationPrompt.System.Should().NotContain("character for character",
            "quote-and-verify is gone (D1): an instruction to copy verbatim makes the model refuse exactly the amounts this phase exists to accept");
    }

    [Fact]
    public void System_prompt_instructs_answering_currency_as_null_rather_than_guessing()
    {
        CategorizationPrompt.System.Should().Contain("only when the message actually states one");
        CategorizationPrompt.System.Should().Contain("answer currency as null rather");
        CategorizationPrompt.System.Should().Contain("than choosing one");
    }

    [Fact]
    public void System_prompt_has_an_example_with_no_stated_currency()
    {
        CategorizationPrompt.System.Should().Contain("The message names no currency");
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
    public void Build_user_turn_gives_today_with_its_weekday_then_the_message_categories_and_hints()
    {
        IReadOnlyList<CategoryOption> categories = [new CategoryOption("groceries", "Groceries", "Продукты", null)];
        var request = new CategorizationRequest("купил вчера штуку евро", new DateOnly(2026, 9, 22), categories, [], []);

        var turn = CategorizationPrompt.BuildUserTurn(request);

        turn.Should().StartWith("Today: 2026-09-22 (Tuesday)");
        turn.Should().Contain("купил вчера штуку евро");
        turn.Should().Contain("groceries: Groceries / Продукты");
        turn.Should().Contain("No known merchants");
    }

    [Fact]
    public void System_prompt_explains_relative_days_are_counted_from_today()
    {
        CategorizationPrompt.System.Should().Contain("occurred_on");
        CategorizationPrompt.System.Should().Contain("counted from today");
    }
}
