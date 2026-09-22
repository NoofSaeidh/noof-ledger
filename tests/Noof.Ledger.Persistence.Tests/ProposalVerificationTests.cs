using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

public class ProposalVerificationTests
{
    static readonly Guid GroceryMerchantId = Guid.NewGuid();
    const string DefaultCurrency = "RSD";

    static ProposedLineItem ValidItem(
        string description = "coffee",
        string amountQuote = "250",
        string? currency = "RSD",
        string categorySlug = "groceries",
        Guid? knownMerchantId = null,
        string? merchantQuote = null) =>
        new(description, amountQuote, currency, categorySlug, knownMerchantId, merchantQuote);

    static bool Resolve(
        string rawText,
        CategorizationProposal proposal,
        out IReadOnlyList<ResolvedLineItem> items,
        out string failure,
        IReadOnlyCollection<string>? offeredSlugs = null,
        IReadOnlyCollection<Guid>? offeredMerchantIds = null,
        string defaultCurrency = DefaultCurrency) =>
        ProposalVerification.TryResolve(
            rawText, proposal, offeredSlugs ?? ["groceries"], offeredMerchantIds ?? [], defaultCurrency,
            out items, out failure);

    [Fact]
    public void An_empty_item_list_resolves_to_no_spending_in_this_message()
    {
        // "заняла у Маши 5000 рсд" (a loan received, not a purchase) is the case that motivates
        // this: the model is instructed to answer with no items at all, and the schema's minItems
        // is 0 so it structurally CAN. An empty proposal is a valid, positive answer - "nothing to
        // categorise here" - not a malformed one, so it must not fail verification.
        var proposal = new CategorizationProposal([]);

        var resolved = Resolve("заняла у Маши 5000 рсд", proposal, out var items, out var failure);

        resolved.Should().BeTrue(failure);
        items.Should().BeEmpty();
        failure.Should().BeEmpty();
    }

    [Fact]
    public void Resolves_a_valid_single_item_proposal()
    {
        var proposal = new CategorizationProposal([ValidItem()]);

        var resolved = Resolve("кофе 250 рсд", proposal, out var items, out var failure);

        resolved.Should().BeTrue();
        failure.Should().BeEmpty();
        items.Should().ContainSingle().Which.Should().Be(
            new ResolvedLineItem("coffee", new Money(250m, CurrencyCode.Rsd), "groceries", null, null));
    }

    [Fact]
    public void A_proposal_with_no_stated_currency_resolves_to_the_default()
    {
        var proposal = new CategorizationProposal([ValidItem(currency: null)]);

        var resolved = Resolve("кофе 250", proposal, out var items, out var failure, defaultCurrency: "RSD");

        resolved.Should().BeTrue(failure);
        items.Should().ContainSingle().Which.Amount.Should().Be(new Money(250m, CurrencyCode.Rsd));
    }

    [Fact]
    public void An_empty_string_currency_also_resolves_to_the_default()
    {
        // The model may answer with an empty string rather than omitting the field entirely -
        // both mean "not stated" and both must land on the default, not be treated as a claimed
        // (and then rejected) currency code.
        var proposal = new CategorizationProposal([ValidItem(currency: "")]);

        var resolved = Resolve("кофе 250", proposal, out var items, out var failure, defaultCurrency: "RSD");

        resolved.Should().BeTrue(failure);
        items.Should().ContainSingle().Which.Amount.Should().Be(new Money(250m, CurrencyCode.Rsd));
    }

    [Fact]
    public void A_proposal_that_states_EUR_resolves_to_EUR_not_the_default()
    {
        // The default must never override a currency the model actually reported - only fill the
        // gap when it reported none.
        var proposal = new CategorizationProposal([ValidItem(currency: "EUR")]);

        var resolved = Resolve("кофе 250 eur", proposal, out var items, out var failure, defaultCurrency: "RSD");

        resolved.Should().BeTrue(failure);
        items.Should().ContainSingle().Which.Amount.Should().Be(new Money(250m, CurrencyCode.Eur));
    }

    [Fact]
    public void A_stated_currency_outside_the_five_is_still_rejected()
    {
        var proposal = new CategorizationProposal([ValidItem(currency: "GBP")]);

        var resolved = Resolve("кофе 250 gbp", proposal, out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void The_default_currency_never_relaxes_the_amount_gate()
    {
        // The currency relaxation changes nothing about the amount verification: an amount quote
        // that does not occur verbatim in the raw text must still fail the whole proposal, whether
        // or not a currency was stated.
        var proposal = new CategorizationProposal([ValidItem(amountQuote: "999", currency: null)]);

        var resolved = Resolve("кофе 250", proposal, out var items, out var failure, defaultCurrency: "RSD");

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void An_amount_quote_that_does_not_occur_verbatim_fails_the_whole_proposal()
    {
        var proposal = new CategorizationProposal([ValidItem(amountQuote: "999")]);

        var resolved = Resolve("кофе 250 рсд", proposal, out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void An_amount_quote_that_is_only_a_fragment_of_a_larger_number_is_refused()
    {
        // The exact trap QuotedAmount itself guards against
        // (QuotedAmountTests.A_quote_that_is_only_a_fragment_of_a_larger_number_fails), re-proven
        // here at the composition level: "500" is a true substring of "1500" but is a different
        // number, not a truncated quote.
        var proposal = new CategorizationProposal([ValidItem(amountQuote: "500")]);

        var resolved = Resolve("кофе 1500 рсд", proposal, out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void A_category_slug_that_was_not_offered_fails()
    {
        var proposal = new CategorizationProposal([ValidItem(categorySlug: "not-offered")]);

        var resolved = Resolve("кофе 250 рсд", proposal, out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().Contain("not-offered");
    }

    [Fact]
    public void A_category_slug_matches_case_insensitively_and_resolves_to_the_offered_casing()
    {
        var proposal = new CategorizationProposal([ValidItem(categorySlug: "GROCERIES")]);

        var resolved = Resolve("кофе 250 рсд", proposal, out var items, out var failure);

        resolved.Should().BeTrue();
        failure.Should().BeEmpty();
        items.Should().ContainSingle().Which.CategorySlug.Should().Be("groceries");
    }

    [Fact]
    public void A_known_merchant_id_that_was_not_offered_fails()
    {
        var proposal = new CategorizationProposal([ValidItem(knownMerchantId: GroceryMerchantId)]);

        var resolved = Resolve("кофе 250 рсд", proposal, out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().Contain(GroceryMerchantId.ToString());
    }

    [Fact]
    public void A_known_merchant_id_that_was_offered_resolves()
    {
        var proposal = new CategorizationProposal([ValidItem(knownMerchantId: GroceryMerchantId)]);

        var resolved = Resolve(
            "кофе 250 рсд", proposal, out var items, out var failure, offeredMerchantIds: [GroceryMerchantId]);

        resolved.Should().BeTrue();
        failure.Should().BeEmpty();
        items.Should().ContainSingle().Which.KnownMerchantId.Should().Be(GroceryMerchantId);
    }

    [Fact]
    public void A_description_longer_than_512_characters_is_truncated_not_rejected()
    {
        var longDescription = new string('a', 600);
        var proposal = new CategorizationProposal([ValidItem(description: longDescription)]);

        var resolved = Resolve("кофе 250 рсд", proposal, out var items, out var failure);

        resolved.Should().BeTrue();
        failure.Should().BeEmpty();
        items.Should().ContainSingle().Which.Description.Length.Should().Be(512);
    }

    [Fact]
    public void A_merchant_quote_longer_than_256_characters_fails_the_whole_proposal()
    {
        var longMerchant = new string('a', 300);
        var proposal = new CategorizationProposal([ValidItem(merchantQuote: longMerchant)]);

        var resolved = Resolve($"кофе 250 рсд у {longMerchant}", proposal, out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void A_merchant_quote_that_does_not_occur_verbatim_in_the_raw_text_fails()
    {
        // The write-once alias table never overwrites (IMerchantDirectory.LinkAliasAsync), so a
        // hallucinated merchant name would live forever. Requiring the same raw-text presence
        // QuotedAmount requires for money closes that door for merchants too.
        var proposal = new CategorizationProposal([ValidItem(merchantQuote: "Ghost Store")]);

        var resolved = Resolve("кофе 250 рсд", proposal, out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().Contain("Ghost Store");
    }

    [Fact]
    public void A_merchant_quote_present_verbatim_resolves()
    {
        var proposal = new CategorizationProposal([ValidItem(merchantQuote: "Maxi")]);

        var resolved = Resolve("кофе 250 рсд у Maxi", proposal, out var items, out var failure);

        resolved.Should().BeTrue();
        failure.Should().BeEmpty();
        items.Should().ContainSingle().Which.MerchantText.Should().Be("Maxi");
    }

    [Fact]
    public void One_bad_item_fails_the_whole_proposal_even_when_an_earlier_item_was_individually_valid()
    {
        // Proves partial acceptance is not offered: the first item alone would resolve fine, but
        // TryResolve must not leak that partial result once the second item fails.
        var proposal = new CategorizationProposal(
        [
            ValidItem(description: "good"),
            ValidItem(description: "bad", amountQuote: "999"),
        ]);

        var resolved = Resolve("кофе 250 рсд", proposal, out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().NotBeEmpty();
    }
}
