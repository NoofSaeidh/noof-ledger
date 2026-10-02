using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Noof.Ledger.Ai.Anthropic;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Ai.Tests;

// Spends real money against the real Anthropic API. Every test starts by checking
// LiveModelGate.TryGetApiKey and calls Assert.Skip when it is false, so this class is silent and
// green in the default `dotnet test` run on a machine with no key set. See LiveModelGate for how
// the key is supplied, and LiveModelGateTests for the guard that keeps that property true.
public sealed class LiveModelTests
{
    static readonly IReadOnlyList<CategoryOption> OfferedCategories =
    [
        new CategoryOption("groceries", "Groceries", "Продукты", null),
        new CategoryOption("food-drink", "Food & Drink", "Еда и напитки", null),
        new CategoryOption("transport", "Transport", "Транспорт", null),
        new CategoryOption("shopping", "Shopping", "Покупки", null),
        new CategoryOption("other", "Other", "Прочее", null),
    ];

    static readonly IReadOnlyList<string> OfferedSlugs = [.. OfferedCategories.Select(c => c.Slug)];

    static readonly IReadOnlyList<WalletOption> OfferedWallets =
        [new WalletOption(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Main Wallet", CurrencyCode.Rsd, [], true)];

    static readonly WalletOption Raiffeisen = new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"), "Raiffeisen RSD", CurrencyCode.Rsd, ["райф"], true);
    // The RSD cash default while Raiffeisen is the RSD default (A-27): an unnamed cash side lands here.
    static readonly WalletOption CashRsd = new(
        Guid.Parse("10000000-0000-0000-0000-000000000002"), "Cash RSD", CurrencyCode.Rsd, ["налик", "наличка"], false,
        DefaultForPayment: WalletPaymentDefault.Cash);
    static readonly WalletOption Wise = new(
        Guid.Parse("10000000-0000-0000-0000-000000000003"), "Wise EUR", CurrencyCode.Eur, ["вайз", "wise"], true);
    static readonly WalletOption Revolut = new(
        Guid.Parse("10000000-0000-0000-0000-000000000004"), "Revolut EUR", CurrencyCode.Eur, ["ревут", "revolut"], false);
    static readonly WalletOption Kaspi = new(
        Guid.Parse("10000000-0000-0000-0000-000000000005"), "Kaspi KZT", CurrencyCode.Kzt, ["каспи"], true);

    // Both the categoriser and the probe go through AnthropicChatClientFactory, never a raw client:
    // the factory is what reads the key through ISecretStore and applies MaxRetries = 0. Building a
    // client here instead would test a client configured differently from the one that actually runs.
    static AnthropicChatClientFactory CreateFactory(string apiKey) =>
        new(new FixedSecretStore(apiKey), new HttpClient(), new AnthropicOptions(),
            new OperationTimer(TimeProvider.System, new SlowOperationOptions()), NullLogger<AnthropicChatClientFactory>.Instance);

    static ChatCategorizer CreateCategorizer(string apiKey) => new(
        CreateFactory(apiKey), new OperationTimer(TimeProvider.System, new SlowOperationOptions()), NullLogger<ChatCategorizer>.Instance);

    static CategorizationRequest Request(string rawText, IReadOnlyList<CategoryOption>? categories = null) =>
        new(rawText, DateOnly.FromDateTime(DateTime.Today), categories ?? OfferedCategories, [], []);

    static CategorizationRequest WithWallets(string rawText) =>
        Request(rawText,
        [
            .. OfferedCategories,
            new CategoryOption("fees-charges", "Fees & Charges", "Комиссии и сборы", null),
            new CategoryOption("books", "Books", "Книги", null),
        ]) with { Wallets = [Raiffeisen, CashRsd, Wise, Revolut, Kaspi] };

    // What the destination wallet actually stores once the ledger settles the model's answer (spec §2, rules 2-3).
    static Money StoredTo(CategorizationProposal proposal)
    {
        var mapped = new ProposalMapper().TryMap(
            proposal, OfferedSlugs, offeredMerchantIds: [], wallets: [Raiffeisen, CashRsd, Wise, Revolut, Kaspi], defaultCurrency: "RSD",
            out var result, out var failure, out _);

        mapped.Should().BeTrue(failure);
        result.Transfer.Should().NotBeNull();
        return result.Transfer!.To;
    }

    [Fact]
    public async Task A_single_coffee_purchase_produces_one_line_item_with_the_amount_and_currency()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        const string rawText = "кофе 250 рсд";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        var item = proposal.Items.Should().ContainSingle().Subject;
        item.Amount.Should().Be(250m);
        item.CurrencyCode.Should().Be("RSD");
    }

    [Fact]
    public async Task A_grocery_run_is_categorised_sensibly_with_the_amount_pinned_not_the_category()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        const string rawText = "продукты 3400 рсд молоко хлеб сыр";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        // The category slug is pinned to "one of the categories we offered", not to a specific
        // slug - the list is operator-editable, and pinning "groceries" here would turn an
        // operator renaming a category into a false failure in this suite.
        var item = proposal.Items.Should().ContainSingle().Subject;
        item.Amount.Should().Be(3400m);
        OfferedSlugs.Should().Contain(item.CategorySlug);
    }

    [Fact]
    public async Task A_message_with_two_amounts_produces_two_line_items()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        const string rawText = "такси 500 рсд, кофе 250 рсд";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        proposal.Items.Should().HaveCount(2);
        proposal.Items.Select(item => item.Amount).Should().BeEquivalentTo([500m, 250m]);
    }

    [Fact]
    public async Task Every_answer_the_model_returns_maps()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        const string rawText = "такси 500 рсд, кофе 250 рсд, продукты 3400 рсд молоко хлеб";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        var mapped = new ProposalMapper().TryMap(
            proposal, OfferedSlugs, offeredMerchantIds: [], wallets: OfferedWallets, defaultCurrency: "RSD", out var result, out var failure, out _);

        mapped.Should().BeTrue(failure);
        result.Items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_named_merchant_is_returned_as_a_merchant_name()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        // No merchant hints are offered (Request passes an empty list), so the schema does not
        // expose known_merchant_id at all - merchant_name is the only way the model can name
        // this merchant.
        const string rawText = "кофе в Starbucks 300 рсд";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        var item = proposal.Items.Should().ContainSingle().Subject;
        item.MerchantName.Should().ContainEquivalentOf("starbucks");
    }

    [Fact]
    public async Task An_amount_said_in_words_is_read_as_a_number()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request("купил штуку евро на продукты"), TestContext.Current.CancellationToken);

        var item = proposal.Items.Should().ContainSingle().Subject;
        item.Amount.Should().Be(1000m);
        item.CurrencyCode.Should().Be("EUR");
    }

    [Fact]
    public async Task A_loan_received_is_recorded_as_income_not_left_unrecorded()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        // I-3 (Phase 4 final review): "заняла у Маши 5000 рсд" states an amount and describes a
        // loan received, not a purchase - but the kind paragraph says a loan you were given is
        // income, so it must post an item under other-income, not nothing at all. An earlier
        // version of this test asserted the opposite (zero items), which is exactly the
        // self-contradiction the review found: the wallet would end up 5000 short of what the
        // bank shows (M1) if the model followed that instruction instead of this one.
        IReadOnlyList<CategoryOption> categoriesWithIncome =
            [.. OfferedCategories, new CategoryOption("other-income", "Other income", "Прочие доходы", null)];
        const string rawText = "заняла у Маши 5000 рсд";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText, categoriesWithIncome), TestContext.Current.CancellationToken);

        proposal.Kind.Should().Be(ProposedKind.Income);
        var item = proposal.Items.Should().ContainSingle().Subject;
        item.Amount.Should().Be(5000m);
        item.CurrencyCode.Should().Be("RSD");
    }

    [Fact]
    public async Task Yesterday_is_answered_as_the_day_before_today()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var today = new DateOnly(2026, 9, 22);
        var proposal = await CreateCategorizer(apiKey).ProposeAsync(
            new CategorizationRequest("купил вчера штуку евро на продукты", today, OfferedCategories, [], []),
            TestContext.Current.CancellationToken);

        proposal.OccurredOn.Should().Be("2026-09-21");
        proposal.Items.Should().ContainSingle().Which.Amount.Should().Be(1000m);
    }

    [Fact]
    public async Task A_withdrawal_is_one_transfer_not_an_expense()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(WithWallets("снял 10000 с райфа"), TestContext.Current.CancellationToken);

        proposal.Kind.Should().Be(ProposedKind.Transfer);
        proposal.Items.Should().BeEmpty();
        proposal.Transfer.Should().NotBeNull();
        var transfer = proposal.Transfer!;
        transfer.FromAmount.Should().Be(10000m);
        transfer.FromCurrency.Should().Be("RSD");
        transfer.ToCurrency.Should().Be("RSD");
        transfer.FromWalletId.Should().Be(Raiffeisen.Id);
        // Left null, 2b puts the cash side in Cash RSD (the RSD cash default); named, it must be Cash RSD itself.
        // Either way it never lands in Raiffeisen, the RSD default.
        (transfer.ToWalletId is null || transfer.ToWalletId == CashRsd.Id).Should().BeTrue($"to_wallet_id was {transfer.ToWalletId}");
    }

    [Fact]
    public async Task A_top_up_between_own_accounts_is_a_transfer()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(WithWallets("пополнил ревут на 200 евро с вайза"), TestContext.Current.CancellationToken);

        proposal.Kind.Should().Be(ProposedKind.Transfer);
        proposal.Transfer.Should().NotBeNull();
        var transfer = proposal.Transfer!;
        transfer.FromAmount.Should().Be(200m);
        transfer.FromWalletId.Should().Be(Wise.Id);
        transfer.ToWalletId.Should().Be(Revolut.Id);
        transfer.FromCurrency.Should().Be("EUR");
        transfer.ToCurrency.Should().Be("EUR");
    }

    [Fact]
    public async Task An_exchange_at_a_stated_rate_copies_the_rate_and_leaves_the_received_amount_null()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(WithWallets("поменял 100 евро на динары по 117"), TestContext.Current.CancellationToken);

        proposal.Kind.Should().Be(ProposedKind.Transfer);
        proposal.Transfer.Should().NotBeNull();
        var transfer = proposal.Transfer!;
        transfer.FromAmount.Should().Be(100m);
        transfer.FromCurrency.Should().Be("EUR");
        transfer.ToCurrency.Should().Be("RSD");
        transfer.ToAmount.Should().BeNull("the model never works out 11700 itself");
        transfer.Rate.Should().Be(new ProposedRate("EUR", 117m, "RSD"));
    }

    [Fact]
    public async Task An_exchange_with_both_amounts_copies_both()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(WithWallets("поменял 100 евро на 11700 динар"), TestContext.Current.CancellationToken);

        proposal.Transfer.Should().NotBeNull();
        var transfer = proposal.Transfer!;
        transfer.FromAmount.Should().Be(100m);
        transfer.ToAmount.Should().Be(11700m);
        transfer.ToCurrency.Should().Be("RSD");
    }

    [Fact]
    public async Task A_fee_on_the_sending_side_is_on_leg_from_and_not_included()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(WithWallets("снял 10000 с райфа, комиссия 150"), TestContext.Current.CancellationToken);

        proposal.Transfer.Should().NotBeNull();
        var transfer = proposal.Transfer!;
        transfer.FromAmount.Should().Be(10000m, "the ledger adds the fee, the model does not");
        transfer.Fee.Should().Be(new ProposedFee(150m, "RSD", ProposedLeg.From, false));
    }

    [Fact]
    public async Task An_amount_said_to_include_the_fee_marks_it_included()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(WithWallets("с райфа списали 10150 при снятии наличных, включая комиссию 150"), TestContext.Current.CancellationToken);

        proposal.Transfer.Should().NotBeNull();
        var transfer = proposal.Transfer!;
        transfer.FromAmount.Should().Be(10150m);
        transfer.Fee.Should().Be(new ProposedFee(150m, "RSD", ProposedLeg.From, true));
    }

    [Fact]
    public async Task A_fee_the_receiving_side_kept_is_on_leg_to()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var proposal = await CreateCategorizer(apiKey).ProposeAsync(
            WithWallets("перевёл 100 евро с вайза на ревут, ревут при зачислении удержал комиссию 1 евро"),
            TestContext.Current.CancellationToken);

        proposal.Transfer.Should().NotBeNull();
        proposal.Transfer!.Fee.Should().NotBeNull();
        var fee = proposal.Transfer.Fee!;
        fee.Leg.Should().Be(ProposedLeg.To);
        fee.Amount.Should().Be(1m);
        fee.Currency.Should().Be("EUR");
    }

    [Fact]
    public async Task A_received_amount_beside_a_fee_on_the_receiving_side_is_stored_as_what_arrived()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        // I-1 (Phase 7 closing review): "получил" is what the purse holds after the office's dinar fee, never 11600.
        var proposal = await CreateCategorizer(apiKey).ProposeAsync(
            WithWallets("поменял 100 евро, получил 11700 динар, комиссия 100 динар"), TestContext.Current.CancellationToken);

        StoredTo(proposal).Should().Be(new Money(11700m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task An_amount_said_only_as_what_arrived_beside_a_fee_is_stored_as_what_arrived()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        // The source is named because an unnamed EUR source would fall to Wise, the EUR default, and put both legs on
        // one wallet. Whichever leg the model gives the fee, Wise must end up with the 9950 that arrived, not 9900.
        var proposal = await CreateCategorizer(apiKey).ProposeAsync(
            WithWallets("перевёл с ревута на вайз, пришло 9950, комиссия 50"), TestContext.Current.CancellationToken);

        StoredTo(proposal).Should().Be(new Money(9950m, CurrencyCode.Eur));
    }

    [Fact]
    public async Task A_stated_charge_is_copied_into_charged_without_arithmetic()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(WithWallets("30 долларов с каспи на книгу, списали 15400"), TestContext.Current.CancellationToken);

        proposal.Kind.Should().Be(ProposedKind.Expense);
        var item = proposal.Items.Should().ContainSingle().Subject;
        item.Amount.Should().Be(30m);
        item.CurrencyCode.Should().Be("USD");
        proposal.WalletId.Should().Be(Kaspi.Id);
        proposal.Charged.Should().NotBeNull();
        var charged = proposal.Charged!;
        charged.Amount.Should().Be(15400m);
        charged.Currency.Should().Be("KZT");
    }

    [Fact]
    public async Task The_key_probe_reports_Ok_for_a_real_key()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var probe = CreateFactory(apiKey);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeTrue(result.Message);
    }

    [Fact]
    public async Task The_key_probe_reports_not_Ok_for_an_obviously_invalid_key()
    {
        if (!LiveModelGate.TryGetApiKey(out _))
            Assert.Skip(LiveModelGate.SkipMessage);

        // Deliberately does not use the real key from the environment - this exercises the
        // rejection path. GET /v1/models costs no tokens whether the key is valid or not, so this
        // carries no cost risk beyond one extra network round trip.
        var probe = CreateFactory("sk-ant-obviously-invalid-0000000000000000000000000000");

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
    }

    // A minimal, read-only ISecretStore standing in for the encrypted app_secret table: it hands
    // back whatever plaintext it was built with for any key asked of it. Nothing about the
    // "secrets live encrypted in the database" rule is being worked around by this - the probe
    // still only ever sees a value through ISecretStore.GetAsync, exactly as it does in
    // production; this fake just supplies a real value that came from the environment for this
    // suite, not a database, because a test process has no database of its own.
    sealed class FixedSecretStore(string plaintext) : ISecretStore
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new SecretResult(SecretState.Present, plaintext));

        public Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new SecretStatus(SecretState.Present, DateTimeOffset.UnixEpoch));

        public Task SetAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake is read-only - the live suite never writes a secret.");

        public Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake is read-only - the live suite never writes a secret.");
    }
}
