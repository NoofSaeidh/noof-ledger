using System.Text.RegularExpressions;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

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
    public void System_prompt_has_between_three_and_seven_examples()
    {
        // Widened from [3,5] (M9): the income and balance examples added below bring the count to 7.
        var opening = Regex.Matches(CategorizationPrompt.System, "<example>").Count;
        var closing = Regex.Matches(CategorizationPrompt.System, "</example>").Count;

        opening.Should().BeInRange(3, 7);
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
    public void System_prompt_explains_kind_and_that_a_balance_has_no_items()
    {
        CategorizationPrompt.System.Should().Contain("\"balance\"");
        CategorizationPrompt.System.Should().Contain("items must be empty");
    }

    [Fact]
    public void System_prompt_explains_wallet_id_falls_back_to_the_currencys_default_wallet()
    {
        CategorizationPrompt.System.Should().Contain("wallet_id");
        CategorizationPrompt.System.Should().Contain("default wallet for the spending's currency");
    }

    [Fact]
    public void System_prompt_has_an_income_example_and_a_balance_example()
    {
        CategorizationPrompt.System.Should().Contain("kind \"income\"");
        CategorizationPrompt.System.Should().Contain("kind \"balance\"");
    }

    [Fact]
    public void System_prompt_records_a_loan_received_as_income_with_one_item_not_zero_items()
    {
        // I-3 (Phase 4 final review): the kind paragraph says a loan you were given is income, so
        // the example must post an item under other-income - otherwise the model is told both "a
        // loan is income" and "record nothing for a loan" in the same prompt, and the wallet ends
        // up short of what the bank shows (M1).
        CategorizationPrompt.System.Should().Contain("заняла у Маши");
        CategorizationPrompt.System.Should().Contain("category_slug the one whose meaning is other income");
        CategorizationPrompt.System.Should().NotContain("Answer with no items at all.");
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
    public void Render_wallets_lists_id_name_currency_aliases_and_the_default_marker()
    {
        IReadOnlyList<WalletOption> wallets =
        [
            new WalletOption(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Cash", CurrencyCode.Rsd, ["наличка", "cash"], true),
        ];

        var rendered = CategorizationPrompt.RenderWallets(wallets);

        rendered.Should().Contain("11111111-1111-1111-1111-111111111111: Cash (RSD)");
        rendered.Should().Contain("also called наличка, cash");
        rendered.Should().Contain("the default wallet for RSD");
    }

    [Fact]
    public void Render_wallets_says_so_when_there_are_none()
    {
        CategorizationPrompt.RenderWallets([]).Should().Contain("wallet_id must be null");
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
    public void Build_user_turn_includes_the_offered_wallets()
    {
        IReadOnlyList<WalletOption> wallets =
            [new WalletOption(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Cash", CurrencyCode.Rsd, [], true)];
        var request = new CategorizationRequest("кофе 250", new DateOnly(2026, 9, 22), [], [], [], Wallets: wallets);

        CategorizationPrompt.BuildUserTurn(request).Should().Contain("Cash (RSD)");
    }

    [Fact]
    public void Build_user_turn_says_no_wallets_are_offered_when_none_are_given()
    {
        var request = new CategorizationRequest("кофе 250", new DateOnly(2026, 9, 22), [], [], []);

        CategorizationPrompt.BuildUserTurn(request).Should().Contain("wallet_id must be null");
    }

    [Fact]
    public void System_prompt_explains_relative_days_are_counted_from_today()
    {
        CategorizationPrompt.System.Should().Contain("occurred_on");
        CategorizationPrompt.System.Should().Contain("counted from today");
    }

    [Fact]
    public void A_correction_adds_the_current_record_and_the_instruction_to_the_user_turn()
    {
        IReadOnlyList<CategoryOption> categories = [new CategoryOption("groceries", "Groceries", "Продукты", null)];
        var current = new RecordedLine("продукты", new Money(1000m, CurrencyCode.Eur), "groceries", "Продукты", "Lidl");
        var request = new CategorizationRequest("купил штуку евро", new DateOnly(2026, 9, 22), categories, [], [],
            new CorrectionRequest(new DateOnly(2026, 9, 21), [current], "нет, 1500"));

        var turn = CategorizationPrompt.BuildUserTurn(request);

        turn.Should().Contain("Current record (dated 2026-09-21):");
        turn.Should().Contain("- продукты: 1000 EUR, category groceries, merchant Lidl");
        // Raw string literals carry the source file's line endings, so assert per line, not on "\n".
        turn.Should().Contain("Correction from the person:");
        turn.Should().EndWith("нет, 1500");
    }

    [Fact]
    public void A_first_reading_has_no_correction_section()
    {
        var request = new CategorizationRequest("кофе 250", new DateOnly(2026, 9, 22), [], [], []);

        CategorizationPrompt.BuildUserTurn(request).Should().NotContain("Correction from the person");
    }

    [Fact]
    public void System_prompt_asks_for_the_complete_corrected_record()
    {
        CategorizationPrompt.System.Should().Contain("complete corrected record");
    }
}
