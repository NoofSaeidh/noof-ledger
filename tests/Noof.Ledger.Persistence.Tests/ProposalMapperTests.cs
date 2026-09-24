using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

public class ProposalMapperTests
{
    static readonly IProposalMapper Mapper = new ProposalMapper();
    static readonly string[] Slugs = ["groceries", "food-drink"];
    static readonly Guid KnownMerchant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    static readonly WalletOption MainRsd = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"), "Main Wallet", CurrencyCode.Rsd, [], IsDefaultForCurrency: true);
    static readonly WalletOption CashRsd = new(
        Guid.Parse("33333333-3333-3333-3333-333333333333"), "Cash", CurrencyCode.Rsd, ["налик"], IsDefaultForCurrency: false);
    static readonly WalletOption WiseEur = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "Wise EUR", CurrencyCode.Eur, ["wise"], IsDefaultForCurrency: true);
    static readonly WalletOption RevolutUsd = new(
        Guid.Parse("44444444-4444-4444-4444-444444444444"), "Revolut USD", CurrencyCode.Usd, [], IsDefaultForCurrency: false);
    static readonly IReadOnlyList<WalletOption> Wallets = [MainRsd, CashRsd, WiseEur, RevolutUsd];

    static ProposedLineItem Line(
        decimal amount, string? currency = "RSD", string slug = "groceries",
        Guid? knownMerchantId = null, string? merchantName = null, string description = "кофе") =>
        new(description, amount, currency, slug, knownMerchantId, merchantName);

    static bool Map(CategorizationProposal proposal, out MappedProposal mapped, out string failure) =>
        MapWith(Wallets, proposal, out mapped, out failure);

    static bool MapWith(
        IReadOnlyList<WalletOption> wallets, CategorizationProposal proposal, out MappedProposal mapped, out string failure) =>
        Mapper.TryMap(proposal, Slugs, [KnownMerchant], wallets, "RSD", out mapped, out failure);

    [Fact]
    public void An_amount_the_message_never_wrote_in_digits_is_taken_as_the_model_gives_it()
    {
        // "купил штуку евро" has no digits at all. Accepting the model's 1000 is the whole point of D1.
        // The model answers a JSON number, so there is nothing here to parse: it is the same decimal
        // System.Text.Json read off the response's "amount" token.
        Map(new([Line(1000m, "EUR")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Amount.Should().Be(new Money(1000m, CurrencyCode.Eur));
    }

    [Theory]
    [InlineData(45.30)]
    [InlineData(0.5)]
    [InlineData(0.1)]
    [InlineData(250)]
    public void A_decimal_amount_maps_to_Money_with_no_precision_loss(decimal amount)
    {
        Map(new([Line(amount)]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Amount.Amount.Should().Be(amount);
    }

    [Fact]
    public void No_currency_and_no_wallet_named_means_the_default_wallet_and_its_currency()
    {
        Map(new([Line(250m, currency: null)]), out var mapped, out _).Should().BeTrue();

        mapped.WalletId.Should().Be(MainRsd.Id);
        mapped.Items.Single().Amount.Currency.Should().Be(CurrencyCode.Rsd);
    }

    [Fact]
    public void A_line_with_no_currency_takes_the_named_wallets_currency()
    {
        Map(new([Line(3.50m, currency: null)], WalletId: WiseEur.Id), out var mapped, out _).Should().BeTrue();

        mapped.WalletId.Should().Be(WiseEur.Id);
        mapped.Items.Single().Amount.Should().Be(new Money(3.50m, CurrencyCode.Eur));
    }

    [Fact]
    public void A_lower_case_currency_maps_to_the_supported_code()
    {
        Map(new([Line(2.50m, currency: "eur")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Amount.Currency.Should().Be(CurrencyCode.Eur);
        mapped.WalletId.Should().Be(WiseEur.Id, "the wallet is matched on the currency whatever its case");
    }

    [Fact]
    public void A_currency_the_ledger_does_not_support_fails()
    {
        Map(new([Line(10m, currency: "GBP")]), out _, out var failure).Should().BeFalse();

        failure.Should().Contain("GBP");
    }

    [Fact]
    public void A_slug_that_was_not_offered_fails_and_a_differently_cased_one_maps_to_the_offered_spelling()
    {
        Map(new([Line(10m, slug: "rent")]), out _, out _).Should().BeFalse();

        Map(new([Line(10m, slug: "Groceries")]), out var mapped, out _).Should().BeTrue();
        mapped.Items.Single().CategorySlug.Should().Be("groceries");
    }

    [Fact]
    public void A_known_merchant_id_that_was_not_offered_fails()
    {
        Map(new([Line(10m, knownMerchantId: Guid.NewGuid())]), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_merchant_name_is_taken_as_given_whether_or_not_the_message_spells_it_that_way()
    {
        Map(new([Line(300m, merchantName: "Starbucks")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().MerchantName.Should().Be("Starbucks");
    }

    [Fact]
    public void A_blank_merchant_name_is_no_merchant()
    {
        Map(new([Line(300m, merchantName: "  ")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().MerchantName.Should().BeNull();
    }

    [Fact]
    public void Over_long_text_is_cut_to_the_column_widths_rather_than_failing_the_job()
    {
        var proposal = new CategorizationProposal([Line(1m, merchantName: new string('m', 300), description: new string('d', 600))]);

        Map(proposal, out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Description.Should().HaveLength(512);
        mapped.Items.Single().MerchantName.Should().HaveLength(256);
    }

    [Fact]
    public void Zero_items_is_a_real_answer()
    {
        Map(new([]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Should().BeEmpty();
        mapped.WalletId.Should().Be(MainRsd.Id, "no line and no currency: the default wallet of the default currency");
    }

    [Fact]
    public void A_named_day_maps_to_that_date()
    {
        Map(new([Line(100m)], "2026-09-20"), out var mapped, out _).Should().BeTrue();

        mapped.OccurredOn.Should().Be(new DateOnly(2026, 9, 20));
    }

    [Fact]
    public void No_day_maps_to_no_date()
    {
        Map(new([Line(100m)]), out var mapped, out _).Should().BeTrue();

        mapped.OccurredOn.Should().BeNull();
    }

    [Theory]
    [InlineData("вчера")]
    [InlineData("20.09.2026")]
    [InlineData("2026-02-30")]
    public void A_day_that_is_not_an_ISO_date_fails(string occurredOn)
    {
        Map(new([Line(100m)], occurredOn), out _, out var failure).Should().BeFalse();

        failure.Should().Contain("occurred_on");
    }

    [Fact]
    public void A_wallet_the_model_named_from_the_offered_ones_is_the_wallet()
    {
        Map(new([Line(250m)], WalletId: CashRsd.Id), out var mapped, out _).Should().BeTrue();

        mapped.WalletId.Should().Be(CashRsd.Id, "a named wallet wins over the currency's default");
    }

    [Fact]
    public void A_wallet_that_was_not_offered_fails()
    {
        var stranger = Guid.Parse("99999999-9999-9999-9999-999999999999");

        Map(new([Line(250m)], WalletId: stranger), out _, out var failure).Should().BeFalse();

        failure.Should().Be($"wallet {stranger} was not offered");
    }

    [Fact]
    public void No_wallet_named_means_the_default_wallet_of_the_first_lines_currency()
    {
        Map(new([Line(3.50m, "EUR"), Line(250m, "RSD")]), out var mapped, out _).Should().BeTrue();

        mapped.WalletId.Should().Be(WiseEur.Id);
        mapped.Items.Select(item => item.Amount.Currency).Should().Equal([CurrencyCode.Eur, CurrencyCode.Rsd],
            "each line keeps its own currency; nothing is converted (M10)");
    }

    [Fact]
    public void A_currency_with_no_default_wallet_falls_back_to_the_default_wallet_of_the_default_currency()
    {
        // Revolut USD exists but is not the USD default, so there is no USD default at all.
        Map(new([Line(20m, "USD")]), out var mapped, out _).Should().BeTrue();

        mapped.WalletId.Should().Be(MainRsd.Id);
        mapped.Items.Single().Amount.Should().Be(new Money(20m, CurrencyCode.Usd), "the spending is not converted (M10)");
    }

    [Fact]
    public void With_no_default_wallet_to_fall_back_to_the_job_fails()
    {
        MapWith([], new([Line(250m)]), out _, out var noWallets).Should().BeFalse();
        MapWith([CashRsd, RevolutUsd], new([Line(250m)]), out _, out var noDefaults).Should().BeFalse();

        noWallets.Should().Be("no wallet to record into");
        noDefaults.Should().Be("no wallet to record into");
    }

    [Theory]
    [InlineData(ProposedKind.Expense, TransactionKind.Expense)]
    [InlineData(ProposedKind.Income, TransactionKind.Income)]
    [InlineData(ProposedKind.Balance, TransactionKind.BalanceCheck)]
    public void Each_kind_maps_to_its_transaction_kind(string kind, TransactionKind expected)
    {
        Map(new([], Kind: kind, BalanceAmount: 100m), out var mapped, out _).Should().BeTrue();

        mapped.Kind.Should().Be(expected);
    }

    [Fact]
    public void A_kind_that_is_not_one_of_the_three_fails()
    {
        Map(new([Line(250m)], Kind: "transfer"), out _, out var failure).Should().BeFalse();

        failure.Should().Contain("transfer");
    }

    [Fact]
    public void An_income_keeps_its_lines_and_carries_no_stated_balance()
    {
        Map(new([Line(2000m, "EUR", description: "зарплата")], Kind: ProposedKind.Income, BalanceAmount: 5m), out var mapped, out _)
            .Should().BeTrue();

        mapped.Kind.Should().Be(TransactionKind.Income);
        mapped.WalletId.Should().Be(WiseEur.Id);
        mapped.Items.Single().Amount.Should().Be(new Money(2000m, CurrencyCode.Eur));
        mapped.StatedBalance.Should().BeNull("only a balance statement states a balance");
    }

    [Fact]
    public void A_balance_statement_maps_its_amount_and_currency_and_picks_the_wallet_by_that_currency()
    {
        Map(new([], Kind: ProposedKind.Balance, BalanceAmount: 3200m, BalanceCurrency: "EUR"), out var mapped, out _)
            .Should().BeTrue();

        mapped.Kind.Should().Be(TransactionKind.BalanceCheck);
        mapped.WalletId.Should().Be(WiseEur.Id);
        mapped.StatedBalance.Should().Be(new Money(3200m, CurrencyCode.Eur));
        mapped.Items.Should().BeEmpty();
    }

    [Fact]
    public void A_balance_statement_ignores_any_lines_the_model_sent_with_it()
    {
        Map(new([Line(250m, "RSD")], Kind: ProposedKind.Balance, BalanceAmount: 3200m, BalanceCurrency: "EUR"), out var mapped, out _)
            .Should().BeTrue();

        mapped.Items.Should().BeEmpty();
        mapped.WalletId.Should().Be(WiseEur.Id, "a stray line's currency must not pick a statement's wallet");
    }

    [Fact]
    public void A_balance_statement_with_no_currency_is_in_its_wallets_currency()
    {
        Map(new([], Kind: ProposedKind.Balance, WalletId: WiseEur.Id, BalanceAmount: 45230.07m), out var mapped, out _)
            .Should().BeTrue();

        mapped.StatedBalance.Should().Be(new Money(45230.07m, CurrencyCode.Eur));
    }

    [Fact]
    public void A_balance_statement_with_no_amount_fails()
    {
        Map(new([], Kind: ProposedKind.Balance, BalanceCurrency: "RSD"), out _, out var failure).Should().BeFalse();

        failure.Should().Be("a balance statement with no amount");
    }

    [Fact]
    public void A_balance_currency_the_ledger_does_not_support_fails()
    {
        Map(new([], Kind: ProposedKind.Balance, WalletId: MainRsd.Id, BalanceAmount: 10m, BalanceCurrency: "GBP"), out _, out var failure)
            .Should().BeFalse();

        failure.Should().Contain("GBP");
    }
}
