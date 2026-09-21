using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class QuotedAmountTests
{
    [Fact]
    public void Quote_present_verbatim_resolves_to_money()
    {
        var resolved = QuotedAmount.TryResolve("кофе 250 рсд", "250", "RSD", out var money, out var failure);

        resolved.Should().BeTrue();
        money.Should().Be(new Money(250m, CurrencyCode.Rsd));
        failure.Should().BeEmpty();
    }

    [Fact]
    public void Quote_not_present_verbatim_fails()
    {
        var resolved = QuotedAmount.TryResolve("кофе двести пятьдесят рсд", "250", "RSD", out var money, out var failure);

        resolved.Should().BeFalse();
        money.Should().Be(default(Money));
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void Missing_currency_code_fails()
    {
        var resolved = QuotedAmount.TryResolve("такси 500", "500", null, out _, out var failure);

        resolved.Should().BeFalse();
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void Unknown_currency_code_fails()
    {
        var resolved = QuotedAmount.TryResolve("такси 500 zzz", "500", "ZZZ", out _, out var failure);

        resolved.Should().BeFalse();
        failure.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("11.700,50")]
    [InlineData("11,700.50")]
    public void European_and_anglo_formats_agree(string quote)
    {
        var rawText = $"продукты {quote} eur";

        var resolved = QuotedAmount.TryResolve(rawText, quote, "EUR", out var money, out _);

        resolved.Should().BeTrue();
        money.Should().Be(new Money(11700.50m, CurrencyCode.Eur));
    }

    [Fact]
    public void A_lone_separator_followed_by_exactly_three_digits_is_a_thousands_group()
    {
        // Ambiguous by construction: "1.700" could be 1700 or 1.7. Pinned to 1700 — see
        // "Design decisions locked by this task" above.
        var resolved = QuotedAmount.TryResolve("аренда 1.700 eur", "1.700", "EUR", out var money, out _);

        resolved.Should().BeTrue();
        money.Should().Be(new Money(1700m, CurrencyCode.Eur));
    }

    [Fact]
    public void A_lone_separator_followed_by_two_digits_is_a_decimal_point()
    {
        var resolved = QuotedAmount.TryResolve("кофе 1.70 eur", "1.70", "EUR", out var money, out _);

        resolved.Should().BeTrue();
        money.Should().Be(new Money(1.70m, CurrencyCode.Eur));
    }

    // Written as explicit \u escapes, not literal glyphs. U+2009 (thin space) and U+00A0
    // (non-breaking space) are visually indistinguishable from a plain space -- and from each
    // other -- in an editor and through copy-paste, which is exactly how an earlier version of
    // this test shipped with both rows byte-identical and silently tested nothing but ASCII
    // space. Never paste a raw special-whitespace glyph into this codebase; escape it.
    [Theory]
    [InlineData("11 700,50")]
    [InlineData("11 700,50")]
    public void Thin_and_non_breaking_space_thousands_separators_are_stripped(string quote)
    {
        var rawText = $"аренда {quote} eur";

        var resolved = QuotedAmount.TryResolve(rawText, quote, "EUR", out var money, out _);

        resolved.Should().BeTrue();
        money.Should().Be(new Money(11700.50m, CurrencyCode.Eur));
    }

    [Theory]
    [InlineData("-250")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12-34")]
    public void Malformed_quotes_fail_cleanly(string quote)
    {
        var rawText = $"заметка {quote} конец";

        var resolved = QuotedAmount.TryResolve(rawText, quote, "RSD", out var money, out var failure);

        resolved.Should().BeFalse();
        money.Should().Be(default(Money));
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void A_quote_repeated_in_the_raw_text_still_resolves()
    {
        var resolved = QuotedAmount.TryResolve("кофе 250, потом ещё кофе 250", "250", "RSD", out var money, out _);

        resolved.Should().BeTrue();
        money.Should().Be(new Money(250m, CurrencyCode.Rsd));
    }
}
