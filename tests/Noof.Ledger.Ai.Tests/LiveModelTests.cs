using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

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

    // Both the categoriser and the probe take IAnthropicClientFactory (Task 3), never a raw client:
    // the factory is what reads the key through ISecretStore and applies MaxRetries = 0. Building a
    // client here instead would test a client configured differently from the one that actually runs.
    static AnthropicClientFactory CreateFactory(string apiKey) =>
        new(new FixedSecretStore(apiKey), new HttpClient(), new AnthropicOptions());

    static AnthropicCategorizer CreateCategorizer(string apiKey) =>
        new(CreateFactory(apiKey), new AnthropicOptions());

    static CategorizationRequest Request(string rawText) =>
        new(rawText, OfferedCategories, [], []);

    [Fact]
    public async Task A_single_coffee_purchase_produces_one_line_item_with_the_literal_amount_and_currency()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        const string rawText = "кофе 250 рсд";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        var item = proposal.Items.Should().ContainSingle().Subject;
        item.AmountQuote.Should().Be("250");
        item.CurrencyCode.Should().Be("RSD");
    }

    [Fact]
    public async Task A_grocery_run_is_categorised_sensibly_with_the_amount_quote_pinned_not_the_category()
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
        item.AmountQuote.Should().Be("3400");
        OfferedSlugs.Should().Contain(item.CategorySlug);
    }

    [Fact]
    public async Task A_message_with_two_amounts_produces_two_line_items_whose_quotes_occur_verbatim()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        const string rawText = "такси 500 рсд, кофе 250 рсд";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        proposal.Items.Should().HaveCount(2);
        foreach (var item in proposal.Items)
            rawText.Should().Contain(item.AmountQuote);
    }

    [Fact]
    public async Task Every_amount_quote_the_model_returns_passes_verification_against_the_raw_text()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        // The gate test that matters most: this is what would catch the real model paraphrasing
        // or normalising a number - "500" becoming "500.00", a separator added or removed, a
        // currency symbol prepended. ProposalVerification.TryResolve is the only gate that stands
        // between a model response and a Money value; if the live model can defeat it here,
        // nothing else in this repository would ever notice.
        const string rawText = "такси 500 рсд, кофе 250 рсд, продукты 3400 рсд молоко хлеб";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        var resolved = ProposalVerification.TryResolve(
            rawText,
            proposal,
            OfferedSlugs,
            offeredMerchantIds: [],
            defaultCurrency: "RSD",
            out var items,
            out var failure);

        resolved.Should().BeTrue(failure);
        items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_named_merchant_returns_a_merchant_quote_that_occurs_in_the_message()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        // No merchant hints are offered (Request passes an empty list), so the schema does not
        // expose known_merchant_id at all - merchant_quote is the only way the model can name
        // this merchant.
        const string rawText = "кофе в Starbucks 300 рсд";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        var item = proposal.Items.Should().ContainSingle().Subject;
        item.MerchantQuote.Should().NotBeNullOrEmpty();
        rawText.Should().Contain(item.MerchantQuote!);
    }

    [Fact]
    public async Task A_loan_received_is_not_spending_and_produces_no_items()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        // The exact case that motivated Defect 1: "заняла у Маши 5000 рсд" states an amount but
        // describes a loan received, not a purchase. CategorizationPrompt's system prompt
        // instructs the model to answer with no items at all for this message, and
        // CategorizationSchema now sets minItems 0 so the model is structurally free to do so.
        // Before that fix the schema forced at least one item, and the model would quote "5000"
        // into a fabricated spend line that ProposalVerification could not tell apart from a real
        // one - money the operator borrowed recorded as money they spent.
        const string rawText = "заняла у Маши 5000 рсд";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        proposal.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task The_key_probe_reports_Ok_for_a_real_key()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var probe = new AnthropicKeyProbe(CreateFactory(apiKey));

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
        var probe = new AnthropicKeyProbe(CreateFactory("sk-ant-obviously-invalid-0000000000000000000000000000"));

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
