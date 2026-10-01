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
        Mapper.TryMap(proposal, Slugs, [KnownMerchant], wallets, "RSD", out mapped, out failure, out _);

    static bool MapTransfer(
        CategorizationProposal proposal, out MappedProposal mapped, out string failure, out RecordFailureReason reason,
        IReadOnlyList<WalletOption>? wallets = null) =>
        Mapper.TryMap(proposal, Slugs, [KnownMerchant], wallets ?? Wallets, "RSD", out mapped, out failure, out reason);

    // The RSD cash and card defaults while Main Wallet stays the RSD default (spec A-27). Kept out of Wallets so every
    // spending test above still sees the wallets it always saw.
    static readonly WalletOption PurseRsd = new(
        Guid.Parse("66666666-6666-6666-6666-666666666666"), "Cash RSD", CurrencyCode.Rsd, ["налик"], IsDefaultForCurrency: false,
        DefaultForPayment: WalletPaymentDefault.Cash);
    static readonly WalletOption RaiffeisenRsd = new(
        Guid.Parse("77777777-7777-7777-7777-777777777777"), "Raiffeisen", CurrencyCode.Rsd, ["райф"], IsDefaultForCurrency: false,
        DefaultForPayment: WalletPaymentDefault.Card);

    static readonly ExchangeRate EuroAt117 = new(CurrencyCode.Eur, 117m, CurrencyCode.Rsd);

    static CategorizationProposal Transfer(
        decimal fromAmount, string fromCurrency = "RSD", string toCurrency = "RSD", decimal? toAmount = null,
        Guid? fromWallet = null, Guid? toWallet = null, ProposedRate? rate = null, ProposedFee? fee = null) =>
        new([], Kind: ProposedKind.Transfer,
            Transfer: new ProposedTransfer(fromWallet, fromAmount, fromCurrency, toWallet, toAmount, toCurrency, rate, fee));

    static Money Rsd(decimal amount) => new(amount, CurrencyCode.Rsd);

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
    public void A_kind_that_is_not_one_of_the_four_fails()
    {
        Map(new([Line(250m)], Kind: "gift"), out _, out var failure).Should().BeFalse();

        failure.Should().Be("kind \"gift\" is not expense, income, balance or transfer.");
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

    [Fact]
    public void A_transfer_between_named_wallets_maps_to_its_settled_legs_and_no_lines()
    {
        MapTransfer(Transfer(10000m, fromWallet: MainRsd.Id, toWallet: CashRsd.Id), out var mapped, out var failure, out var reason)
            .Should().BeTrue();

        failure.Should().BeEmpty();
        reason.Should().Be(RecordFailureReason.None);
        mapped.Kind.Should().Be(TransactionKind.Transfer);
        mapped.WalletId.Should().Be(MainRsd.Id, "transactions.wallet_id holds the source");
        mapped.Items.Should().BeEmpty();
        mapped.Transfer.Should().Be(new TransferFacts(MainRsd.Id, Rsd(10000m), CashRsd.Id, Rsd(10000m), null, null, null));
    }

    [Fact]
    public void A_leg_with_no_named_wallet_takes_the_default_wallet_of_its_own_currency()
    {
        MapTransfer(Transfer(100m, "EUR", "RSD", 11700m), out var mapped, out _, out _).Should().BeTrue();

        mapped.Transfer.Should().Be(new TransferFacts(
            WiseEur.Id, new Money(100m, CurrencyCode.Eur), MainRsd.Id, Rsd(11700m), null, null, null));
    }

    [Fact]
    public void An_unnamed_destination_goes_to_the_cash_wallet_of_its_currency_before_the_currencys_default()
    {
        // "снял 10000 с райфа": the bank is named, the cash side is not.
        MapTransfer(Transfer(10000m, fromWallet: MainRsd.Id), out var mapped, out _, out var reason, [.. Wallets, PurseRsd, RaiffeisenRsd])
            .Should().BeTrue();

        reason.Should().Be(RecordFailureReason.None);
        mapped.Transfer.Should().Be(new TransferFacts(MainRsd.Id, Rsd(10000m), PurseRsd.Id, Rsd(10000m), null, null, null));
    }

    [Fact]
    public void A_bare_withdrawal_takes_from_the_card_default_and_lands_in_the_cash_default()
    {
        // "снял 10000": neither side named. Under one cash rule for both legs this was SameWallet.
        MapTransfer(Transfer(10000m), out var mapped, out _, out var reason, [.. Wallets, PurseRsd, RaiffeisenRsd])
            .Should().BeTrue();

        reason.Should().Be(RecordFailureReason.None);
        mapped.Transfer.Should().Be(new TransferFacts(RaiffeisenRsd.Id, Rsd(10000m), PurseRsd.Id, Rsd(10000m), null, null, null));
    }

    [Fact]
    public void An_unnamed_source_never_takes_the_cash_wallet()
    {
        // No RSD card default: the source falls to the RSD default, not to Cash RSD.
        MapTransfer(Transfer(10000m), out var mapped, out _, out _, [.. Wallets, PurseRsd]).Should().BeTrue();

        (mapped.Transfer?.FromWalletId).Should().Be(MainRsd.Id);
        (mapped.Transfer?.ToWalletId).Should().Be(PurseRsd.Id);
    }

    [Fact]
    public void A_top_up_naming_only_the_card_default_is_SameWallet()
    {
        // "положил 20000 на райф": the unnamed source takes the RSD card default, which is the named destination.
        // A top-up from cash has to name the cash wallet.
        MapTransfer(Transfer(20000m, toWallet: RaiffeisenRsd.Id), out _, out var failure, out var reason, [.. Wallets, PurseRsd, RaiffeisenRsd])
            .Should().BeFalse();

        reason.Should().Be(RecordFailureReason.SameWallet);
        failure.Should().Be("SameWallet");
    }

    [Fact]
    public void An_unnamed_leg_with_no_payment_default_in_its_currency_takes_the_currencys_default()
    {
        MapTransfer(Transfer(100m, "EUR", "RSD", 11700m), out var mapped, out _, out _, [.. Wallets, PurseRsd, RaiffeisenRsd]).Should().BeTrue();

        (mapped.Transfer?.FromWalletId).Should().Be(WiseEur.Id, "there is no EUR card wallet");
        (mapped.Transfer?.ToWalletId).Should().Be(PurseRsd.Id);
    }

    [Fact]
    public void A_leg_whose_currency_has_no_default_wallet_is_a_currency_mismatch_not_the_default_currencys_wallet()
    {
        // Revolut USD exists but is not the USD default; a spending would fall back to RSD's default, a leg must not.
        MapTransfer(Transfer(20m, "USD", "RSD", 2300m), out _, out var failure, out var reason).Should().BeFalse();

        reason.Should().Be(RecordFailureReason.LegCurrencyMismatch);
        failure.Should().Be("LegCurrencyMismatch");
    }

    [Fact]
    public void A_named_wallet_in_another_currency_than_its_leg_is_a_currency_mismatch()
    {
        MapTransfer(Transfer(100m, "EUR", "RSD", 11700m, fromWallet: CashRsd.Id), out _, out _, out var reason).Should().BeFalse();

        reason.Should().Be(RecordFailureReason.LegCurrencyMismatch);
    }

    [Fact]
    public void Both_legs_on_one_wallet_is_SameWallet()
    {
        MapTransfer(Transfer(10000m), out _, out var failure, out var defaultedReason).Should().BeFalse();
        MapTransfer(Transfer(10000m, fromWallet: CashRsd.Id, toWallet: CashRsd.Id), out _, out _, out var namedReason).Should().BeFalse();

        defaultedReason.Should().Be(RecordFailureReason.SameWallet, "with no RSD card or cash default both legs fall to the one RSD default");
        failure.Should().Be("SameWallet");
        namedReason.Should().Be(RecordFailureReason.SameWallet);
    }

    [Fact]
    public void A_leg_wallet_that_was_not_offered_fails_with_no_reason()
    {
        var stranger = Guid.Parse("99999999-9999-9999-9999-999999999999");

        MapTransfer(Transfer(10000m, fromWallet: stranger, toWallet: CashRsd.Id), out _, out var failure, out var reason).Should().BeFalse();

        failure.Should().Be($"wallet {stranger} was not offered");
        reason.Should().Be(RecordFailureReason.None);
    }

    [Fact]
    public void A_leg_currency_the_ledger_does_not_support_fails_with_no_reason()
    {
        MapTransfer(Transfer(10m, "GBP"), out _, out var failure, out var reason).Should().BeFalse();

        failure.Should().Be("transfer from_currency \"GBP\" is not one this ledger supports.");
        reason.Should().Be(RecordFailureReason.None);
    }

    [Fact]
    public void A_rate_settles_the_received_amount_and_is_kept_as_stated()
    {
        MapTransfer(Transfer(100m, "EUR", "RSD", rate: new ProposedRate("EUR", 117m, "RSD")), out var mapped, out _, out _)
            .Should().BeTrue();

        mapped.Transfer.Should().Be(new TransferFacts(
            WiseEur.Id, new Money(100m, CurrencyCode.Eur), MainRsd.Id, Rsd(11700m), null, null,
            new ExchangeRate(CurrencyCode.Eur, 117m, CurrencyCode.Rsd)));
    }

    [Fact]
    public void A_valid_rate_beside_a_received_amount_is_kept_as_stated_while_the_amount_settles()
    {
        MapTransfer(Transfer(100m, "EUR", "RSD", 11650m, rate: new ProposedRate("EUR", 117m, "RSD")), out var mapped, out _, out _)
            .Should().BeTrue();

        (mapped.Transfer?.To).Should().Be(Rsd(11650m), "the received amount said wins for the settlement");
        (mapped.Transfer?.StatedRate).Should().Be(EuroAt117, "amendment 24: a valid stated rate is kept whenever it was said");
    }

    [Fact]
    public void A_rate_that_cannot_convert_beside_a_received_amount_is_dropped_not_failed()
    {
        MapTransfer(Transfer(100m, "EUR", "RSD", 11650m, rate: new ProposedRate("USD", 117m, "RSD")), out var mapped, out _, out var reason)
            .Should().BeTrue();

        reason.Should().Be(RecordFailureReason.None);
        (mapped.Transfer?.To).Should().Be(Rsd(11650m));
        (mapped.Transfer?.StatedRate).Should().BeNull();
    }

    [Fact]
    public void A_rate_in_a_currency_the_ledger_does_not_support_fails_with_no_reason()
    {
        MapTransfer(Transfer(100m, "EUR", "RSD", rate: new ProposedRate("GBP", 1.17m, "EUR")), out _, out var failure, out var reason)
            .Should().BeFalse();

        failure.Should().Be("rate currency \"GBP\" is not one this ledger supports.");
        reason.Should().Be(RecordFailureReason.None);
    }

    [Fact]
    public void A_rate_that_is_not_the_legs_currencies_fails_with_InvalidRate()
    {
        MapTransfer(Transfer(100m, "EUR", "RSD", rate: new ProposedRate("USD", 117m, "RSD")), out _, out var failure, out var reason)
            .Should().BeFalse();

        reason.Should().Be(RecordFailureReason.InvalidRate);
        failure.Should().Be("InvalidRate");
    }

    [Fact]
    public void An_exchange_with_no_received_amount_and_no_rate_fails_with_MissingReceivedAmount()
    {
        MapTransfer(Transfer(100m, "EUR", "RSD"), out _, out var failure, out var reason).Should().BeFalse();

        reason.Should().Be(RecordFailureReason.MissingReceivedAmount);
        failure.Should().Be("MissingReceivedAmount");
    }

    [Fact]
    public void A_fee_becomes_the_fee_of_its_leg_inside_that_legs_stored_amount()
    {
        var proposal = Transfer(10000m, fromWallet: MainRsd.Id, toWallet: CashRsd.Id,
            fee: new ProposedFee(150m, "RSD", ProposedLeg.From, false));

        MapTransfer(proposal, out var mapped, out _, out _).Should().BeTrue();

        mapped.Transfer.Should().Be(new TransferFacts(
            MainRsd.Id, Rsd(10150m), CashRsd.Id, Rsd(10000m), Rsd(150m), TransferLeg.From, null));
    }

    [Fact]
    public void A_fee_leg_is_read_whatever_its_casing()
    {
        var proposal = Transfer(10000m, fromWallet: MainRsd.Id, toWallet: CashRsd.Id, fee: new ProposedFee(150m, "RSD", "TO", false));

        MapTransfer(proposal, out var mapped, out _, out _).Should().BeTrue();

        (mapped.Transfer?.FeeLeg).Should().Be(TransferLeg.To);
    }

    [Fact]
    public void A_fee_on_a_leg_that_is_neither_from_nor_to_fails_with_no_reason()
    {
        var proposal = Transfer(10000m, fromWallet: MainRsd.Id, toWallet: CashRsd.Id, fee: new ProposedFee(150m, "RSD", "both", false));

        MapTransfer(proposal, out _, out var failure, out var reason).Should().BeFalse();

        failure.Should().Be("fee leg \"both\" is not from or to.");
        reason.Should().Be(RecordFailureReason.None);
    }

    [Fact]
    public void A_fee_said_in_only_the_destinations_currency_is_on_the_destination_whatever_leg_the_model_named()
    {
        // "поменял 100 евро по 117, комиссия 150 динар" (amendment 23)
        var proposal = Transfer(100m, "EUR", "RSD", rate: new ProposedRate("EUR", 117m, "RSD"),
            fee: new ProposedFee(150m, "RSD", ProposedLeg.From, false));

        MapTransfer(proposal, out var mapped, out _, out _).Should().BeTrue();

        mapped.Transfer.Should().Be(new TransferFacts(
            WiseEur.Id, new Money(100m, CurrencyCode.Eur), MainRsd.Id, Rsd(11550m), Rsd(150m), TransferLeg.To, EuroAt117));
    }

    [Fact]
    public void A_fee_said_in_only_the_sources_currency_is_on_the_source_whatever_leg_the_model_named()
    {
        var proposal = Transfer(100m, "EUR", "RSD", rate: new ProposedRate("EUR", 117m, "RSD"),
            fee: new ProposedFee(2m, "EUR", ProposedLeg.To, false));

        MapTransfer(proposal, out var mapped, out _, out _).Should().BeTrue();

        mapped.Transfer.Should().Be(new TransferFacts(
            WiseEur.Id, new Money(102m, CurrencyCode.Eur), MainRsd.Id, Rsd(11700m), new Money(2m, CurrencyCode.Eur),
            TransferLeg.From, EuroAt117));
    }

    [Fact]
    public void A_fee_in_neither_sides_currency_fails_with_InvalidFee()
    {
        var proposal = Transfer(10000m, fromWallet: MainRsd.Id, toWallet: CashRsd.Id, fee: new ProposedFee(1m, "EUR", ProposedLeg.From, false));

        MapTransfer(proposal, out _, out _, out var reason).Should().BeFalse();

        reason.Should().Be(RecordFailureReason.InvalidFee);
    }

    // Amendment 24's round trip, last step. 2a renders a withdrawal stored as 10150 → 10000 with the 150 fee on the source
    // as "- from …: 10000 RSD, plus a fee of 150 RSD on this side (not included in the figure)" and "- to …: 10000 RSD,
    // worked out by the ledger"; answered back unchanged (the worker keeps both wallets) it settles to the stored legs.
    [Fact]
    public void A_withdrawal_answered_back_unchanged_settles_to_the_stored_legs()
    {
        var unchanged = Transfer(10000m, fromWallet: MainRsd.Id, toWallet: CashRsd.Id,
            fee: new ProposedFee(150m, "RSD", ProposedLeg.From, false));

        MapTransfer(unchanged, out var mapped, out _, out _).Should().BeTrue();

        mapped.Transfer.Should().Be(new TransferFacts(
            MainRsd.Id, Rsd(10150m), CashRsd.Id, Rsd(10000m), Rsd(150m), TransferLeg.From, null));
    }

    // The exchange 2a renders and reads back (An_exchange_answered_back_unchanged_reads_into_the_proposal_the_rendering_implies):
    // stored 100 EUR → 11562.35 RSD, 150 RSD fee on the destination, stated rate 117.1235. "это было позавчера" answered
    // back unchanged moves the day and keeps every figure and the rate - also when the model repeats the worked-out
    // 11712.35 instead of leaving it null.
    [Fact]
    public void A_date_only_correction_answered_back_unchanged_keeps_the_legs_fee_and_stated_rate()
    {
        var stored = new TransferFacts(
            WiseEur.Id, new Money(100m, CurrencyCode.Eur), MainRsd.Id, Rsd(11562.35m), Rsd(150m), TransferLeg.To,
            new ExchangeRate(CurrencyCode.Eur, 117.1235m, CurrencyCode.Rsd));
        var said = new ProposedTransfer(
            WiseEur.Id, 100m, "EUR", MainRsd.Id, null, "RSD",
            new ProposedRate("EUR", 117.1235m, "RSD"), new ProposedFee(150m, "RSD", ProposedLeg.To, false));
        var unchanged = new CategorizationProposal([], OccurredOn: "2026-09-20", Kind: ProposedKind.Transfer, Transfer: said);
        var repeated = unchanged with { Transfer = said with { ToAmount = 11712.35m } };

        MapTransfer(unchanged, out var mapped, out _, out _).Should().BeTrue();
        MapTransfer(repeated, out var mappedRepeated, out _, out _).Should().BeTrue();

        mapped.Transfer.Should().Be(stored);
        mapped.OccurredOn.Should().Be(new DateOnly(2026, 9, 20));
        mappedRepeated.Transfer.Should().Be(stored);
    }

    [Fact]
    public void A_transfer_amount_that_is_not_positive_fails_with_InvalidAmount()
    {
        MapTransfer(Transfer(0m, fromWallet: MainRsd.Id, toWallet: CashRsd.Id), out _, out _, out var reason).Should().BeFalse();

        reason.Should().Be(RecordFailureReason.InvalidAmount);
    }

    [Fact]
    public void A_transfer_keeps_no_line_items_even_when_the_model_sent_some()
    {
        var proposal = Transfer(10000m, fromWallet: MainRsd.Id, toWallet: CashRsd.Id) with { Items = [Line(250m)] };

        MapTransfer(proposal, out var mapped, out _, out _).Should().BeTrue();

        mapped.Items.Should().BeEmpty();
    }

    [Fact]
    public void A_transfer_kind_with_no_transfer_object_fails_with_no_reason()
    {
        MapTransfer(new([], Kind: ProposedKind.Transfer), out _, out var failure, out var reason).Should().BeFalse();

        failure.Should().Be("a transfer with no transfer object");
        reason.Should().Be(RecordFailureReason.None);
    }

    [Fact]
    public void A_transfer_takes_the_day_it_names()
    {
        var proposal = Transfer(10000m, fromWallet: MainRsd.Id, toWallet: CashRsd.Id) with { OccurredOn = "2026-09-20" };

        MapTransfer(proposal, out var mapped, out _, out _).Should().BeTrue();

        mapped.OccurredOn.Should().Be(new DateOnly(2026, 9, 20));
    }

    [Fact]
    public void A_spending_still_maps_with_no_reason()
    {
        Mapper.TryMap(new([Line(250m)]), Slugs, [KnownMerchant], Wallets, "RSD", out _, out var failure, out var reason)
            .Should().BeTrue();

        failure.Should().BeEmpty();
        reason.Should().Be(RecordFailureReason.None);
    }
}
