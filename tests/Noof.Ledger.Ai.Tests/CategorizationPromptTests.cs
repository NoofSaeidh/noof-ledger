using System.Text.RegularExpressions;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.TestKit;

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
    public void System_prompt_scopes_the_null_currency_rule_to_an_items_currency()
    {
        // A transfer side's currency is never null in the schema, so the null rule must not reach it.
        CategorizationPrompt.System.Should().Contain("item's currency only: each side of a transfer always has a currency");
        CategorizationPrompt.System.Should().Contain("says or else the named wallet's own");
    }

    [Fact]
    public void System_prompt_has_between_three_and_ten_examples()
    {
        // Widened from [3,7] (Phase 7): a withdrawal with a fee, an exchange at a stated rate and a stated charge
        // bring the count to 10.
        var opening = Regex.Matches(CategorizationPrompt.System, "<example>").Count;
        var closing = Regex.Matches(CategorizationPrompt.System, "</example>").Count;

        opening.Should().BeInRange(3, 10);
        closing.Should().Be(opening);
    }

    [Fact]
    public void System_prompt_says_money_between_own_wallets_is_one_transfer()
    {
        CategorizationPrompt.System.Should().Contain("are all kind \"transfer\", never an expense plus an income");
        CategorizationPrompt.System.Should().Contain("transfer is null for");
    }

    [Fact]
    public void System_prompt_forbids_arithmetic_and_routes_a_rate_and_a_fee_to_their_fields()
    {
        CategorizationPrompt.System.Should().Contain("never multiply, add or subtract");
        CategorizationPrompt.System.Should().Contain("a stated rate goes into rate");
        CategorizationPrompt.System.Should().Contain("a fee goes into fee, and the ledger works out the");
        CategorizationPrompt.System.Should().Contain("Leave to_amount null when the person did not say what arrived");
        CategorizationPrompt.System.Should().Contain("Do not work out 11700 yourself.");
    }

    [Fact]
    public void System_prompt_puts_a_fee_on_from_and_marks_it_included_only_when_said()
    {
        CategorizationPrompt.System.Should().Contain("A fee is on leg \"from\" unless the person says the receiving side kept it");
        CategorizationPrompt.System.Should().Contain("true only when the person says the amount they gave for that side");
    }

    [Fact]
    public void System_prompt_answers_charged_only_when_the_charged_amount_is_said()
    {
        CategorizationPrompt.System.Should().Contain("Answer charged only when the person says what was actually taken from the wallet");
        CategorizationPrompt.System.Should().Contain("Otherwise charged is");
    }

    [Fact]
    public void System_prompt_explains_the_current_record_of_a_correction_and_a_failed_first_reading()
    {
        CategorizationPrompt.System.Should().Contain("answer charged only when the correction states a new one");
        CategorizationPrompt.System.Should().Contain("the first reading could not be recorded");
    }

    [Fact]
    public void System_prompt_explains_how_a_transfers_current_sides_are_answered_back()
    {
        // A-21: a fee shown beside its side's principal is answered not included; a side the ledger worked
        // out is answered null, so a date-only correction re-derives it instead of pinning a rounded figure.
        CategorizationPrompt.System.Should().Contain("is answered with included false");
        CategorizationPrompt.System.Should().Contain("A side \"worked out by the ledger\" is answered");
        CategorizationPrompt.System.Should().Contain("with to_amount null unless the correction states what arrived");
    }

    [Fact]
    public void System_prompt_leaves_an_unnamed_side_to_the_ledgers_card_or_cash_wallet()
    {
        CategorizationPrompt.System.Should().Contain("the ledger takes an unnamed from side out of the card wallet of its currency");
        CategorizationPrompt.System.Should().Contain("an unnamed to side in the cash wallet of its currency, each else in the default.");
        CategorizationPrompt.System.Should().Contain("The person names no wallet for the cash, so to_wallet_id stays null.");
    }

    [Fact]
    public void System_prompt_puts_a_fee_said_in_one_sides_currency_on_that_side()
    {
        CategorizationPrompt.System.Should().Contain("side's currency is on that side");
        CategorizationPrompt.System.Should().Contain("\"комиссия 150 динар\" on euros changed into dinars is leg");
    }

    [Fact]
    public void System_prompt_has_a_withdrawal_an_exchange_and_a_stated_charge_example()
    {
        CategorizationPrompt.System.Should().Contain("Message: \"снял 10000 с райфа, комиссия 150\"");
        CategorizationPrompt.System.Should().Contain("Message: \"поменял 100 евро на динары по 117\"");
        CategorizationPrompt.System.Should().Contain("Message: \"30 долларов с каспи на книгу, списали 15400\"");
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

    static readonly DateOnly Recorded = new(2026, 9, 21);

    // Stored as T-12 says: 10150 left Raiffeisen (the 150 fee inside it), 10000 reached the cash.
    static readonly TransferView Withdrawal = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "Raiffeisen RSD", new Money(10150m, CurrencyCode.Rsd),
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "Cash RSD", new Money(10000m, CurrencyCode.Rsd),
        new Money(150m, CurrencyCode.Rsd), TransferLeg.From, null, null, [], []);

    // 100 EUR at a stated 117.1235 is 11712.35 RSD; the office kept 150 RSD, so 11562.35 arrived.
    static readonly TransferView Exchange = new(
        Guid.Parse("33333333-3333-3333-3333-333333333333"), "Cash EUR", new Money(100m, CurrencyCode.Eur),
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "Cash RSD", new Money(11562.35m, CurrencyCode.Rsd),
        new Money(150m, CurrencyCode.Rsd), TransferLeg.To, new ExchangeRate(CurrencyCode.Eur, 117.1235m, CurrencyCode.Rsd),
        null, [], []);

    static string TurnFor(CorrectionRequest correction) =>
        CategorizationPrompt.BuildUserTurn(new CategorizationRequest("raw", new DateOnly(2026, 9, 22), [], [], [], correction));

    [Fact]
    public void A_correction_of_a_withdrawal_states_each_side_as_said_with_the_fee_beside_its_side()
    {
        var turn = TurnFor(new CorrectionRequest(Recorded, [], "нет, 12000", TransactionKind.Transfer, Withdrawal));

        turn.Should().Contain("Kind: transfer");
        turn.Should().Contain("- from Raiffeisen RSD: 10000 RSD, plus a fee of 150 RSD on this side (not included in the figure)");
        turn.Should().Contain("- to Cash RSD: 10000 RSD, worked out by the ledger");
        turn.Should().NotContain("10150", "the stored amount with the fee inside would be read back as said and charged twice");
        turn.Should().NotContain("nothing was recorded");
        turn.Should().NotContain("no line items", "a transfer has no principal lines by definition");
    }

    [Fact]
    public void A_correction_of_an_exchange_marks_the_worked_out_side_and_keeps_the_rate_as_stated()
    {
        var turn = TurnFor(new CorrectionRequest(Recorded, [], "это было позавчера", TransactionKind.Transfer, Exchange));

        turn.Should().Contain("- from Cash EUR: 100 EUR");
        turn.Should().Contain(
            "- to Cash RSD: 11712.35 RSD, worked out by the ledger, plus a fee of 150 RSD on this side (not included in the figure)");
        turn.Should().Contain("- rate as stated: 1 EUR = 117.1235 RSD");
    }

    [Fact]
    public void A_received_amount_the_rate_does_not_give_is_shown_as_said()
    {
        var said = Exchange with { To = new Money(11650m, CurrencyCode.Rsd), Fee = null, FeeLeg = null };

        var turn = TurnFor(new CorrectionRequest(Recorded, [], "нет", TransactionKind.Transfer, said));

        turn.Should().Contain("- to Cash RSD: 11650 RSD");
        turn.Should().NotContain("worked out by the ledger");
    }

    [Fact]
    public void An_exchange_with_both_amounts_said_and_no_rate_shows_both_as_said()
    {
        var said = Exchange with { To = new Money(11700m, CurrencyCode.Rsd), Fee = null, FeeLeg = null, StatedRate = null };

        var turn = TurnFor(new CorrectionRequest(Recorded, [], "нет", TransactionKind.Transfer, said));

        turn.Should().Contain("- from Cash EUR: 100 EUR");
        turn.Should().Contain("- to Cash RSD: 11700 RSD");
        turn.Should().NotContain("worked out by the ledger");
        turn.Should().NotContain("rate as stated");
    }

    [Fact]
    public void A_correction_of_a_foreign_spending_shows_each_charge_as_a_fact()
    {
        var book = new RecordedLine("книга", new Money(30m, CurrencyCode.Usd), "shopping", "Shopping", null);
        IReadOnlyList<ChargeView> charges =
        [
            new(CurrencyCode.Usd, 30m, new Money(15400m, CurrencyCode.Kzt), new Money(154m, CurrencyCode.Kzt),
                513.333333333333m, new FeeTerms(1m, null, null), ChargeSource.Stated),
            new(CurrencyCode.Eur, 20m, new Money(11000m, CurrencyCode.Kzt), new Money(0m, CurrencyCode.Kzt),
                550m, FeeTerms.None, ChargeSource.WalletTerms),
        ];

        var turn = TurnFor(new CorrectionRequest(Recorded, [book], "это подарок", TransactionKind.Expense, null, charges));

        turn.Should().Contain("Kind: expense");
        turn.Should().Contain("- книга: 30 USD, category shopping");
        turn.Should().Contain("- the USD lines (30 USD) were charged 15400 KZT plus a fee of 154 KZT to the wallet, as the person stated");
        turn.Should().Contain("- the EUR lines (20 EUR) were charged 11000 KZT to the wallet, at the wallet's own rate");
    }

    [Fact]
    public void A_correction_of_an_income_names_its_kind()
    {
        var salary = new RecordedLine("зарплата", new Money(2000m, CurrencyCode.Eur), "salary", "Salary", null);

        TurnFor(new CorrectionRequest(Recorded, [salary], "нет, 2100", TransactionKind.Income)).Should().Contain("Kind: income");
    }

    [Fact]
    public void A_correction_of_a_balance_check_shows_the_stated_balance()
    {
        var statement = new BalanceStatement(new Money(45000m, CurrencyCode.Rsd), 44800m);

        var turn = TurnFor(new CorrectionRequest(Recorded, [], "нет, 46000", TransactionKind.BalanceCheck, CurrentStatement: statement));

        turn.Should().Contain("Kind: balance");
        turn.Should().Contain("- stated balance: 45000 RSD");
        turn.Should().NotContain("44800", "what the app had computed is history for the echo, not something the person said");
        turn.Should().NotContain("no line items");
    }

    [Fact]
    public void A_record_no_reading_ever_completed_is_nothing_recorded_with_no_kind()
    {
        // A failed first reading: never applied, so its kind is the default Expense and it holds nothing. Naming
        // that kind would push a reply such as "11700" toward an expense.
        var turn = TurnFor(new CorrectionRequest(Recorded, [], "11700"));

        turn.Should().Contain("- nothing was recorded");
        turn.Should().NotContain("Kind:");
    }

    [Fact]
    public void Transfer_amounts_render_the_same_under_a_Russian_machine_culture()
    {
        using var culture = new CultureScope("ru-RU");

        TurnFor(new CorrectionRequest(Recorded, [], "нет", TransactionKind.Transfer, Exchange))
            .Should().Contain("- to Cash RSD: 11712.35 RSD, worked out by the ledger");
    }
}
