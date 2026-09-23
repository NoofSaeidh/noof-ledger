using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

public class ProposalMapperTests
{
    static readonly IProposalMapper Mapper = new ProposalMapper();
    static readonly string[] Slugs = ["groceries", "food-drink"];
    static readonly Guid KnownMerchant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    static ProposedLineItem Line(
        decimal amount, string? currency = "RSD", string slug = "groceries",
        Guid? knownMerchantId = null, string? merchantName = null, string description = "кофе") =>
        new(description, amount, currency, slug, knownMerchantId, merchantName);

    static bool Map(CategorizationProposal proposal, out MappedProposal mapped, out string failure) =>
        Mapper.TryMap(proposal, Slugs, [KnownMerchant], "RSD", out mapped, out failure);

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
    public void No_currency_means_the_configured_default()
    {
        Map(new([Line(250m, currency: null)]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Amount.Currency.Should().Be(CurrencyCode.Rsd);
    }

    [Fact]
    public void A_lower_case_currency_maps_to_the_supported_code()
    {
        Map(new([Line(2.50m, currency: "eur")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Amount.Currency.Should().Be(CurrencyCode.Eur);
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
}
