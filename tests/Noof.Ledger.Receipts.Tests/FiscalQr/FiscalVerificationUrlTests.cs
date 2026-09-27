using AwesomeAssertions;
using Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Receipts.Tests.FiscalQr;

public class FiscalVerificationUrlTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://suf.purs.gov.rs/v/?vl=")]
    public void Rejects_a_bad_verification_url_prefix_at_construction(string prefix)
    {
        var act = () => new FiscalVerificationUrl(new FiscalVerificationUrlOptions { VerificationUrlPrefix = prefix });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{FiscalVerificationUrlOptions.ConfigurationSection}:{nameof(FiscalVerificationUrlOptions.VerificationUrlPrefix)}*");
    }

    // Minor finding (Fable 5.1 review): "https://host/v?vl=" derives PathPrefix "/v", and
    // FiscalQrDecoder's StartsWith(PathPrefix) then admits "/verify/..." too - a segment prefix, not a
    // path prefix. Failing fast here, rather than normalising by appending a slash, keeps the one
    // validation site the operator sees when Receipts:VerificationUrlPrefix is wrong.
    [Fact]
    public void Rejects_a_verification_url_prefix_whose_path_has_no_trailing_slash()
    {
        var act = () => new FiscalVerificationUrl(
            new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v?vl=" });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{FiscalVerificationUrlOptions.ConfigurationSection}:{nameof(FiscalVerificationUrlOptions.VerificationUrlPrefix)}*")
            .WithMessage("*end with a slash*");
    }

    [Fact]
    public void Derives_the_host_and_path_prefix_from_a_valid_url()
    {
        var verificationUrl =
            new FiscalVerificationUrl(new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });

        verificationUrl.Host.Should().Be("suf.purs.gov.rs");
        verificationUrl.PathPrefix.Should().Be("/v/");
        verificationUrl.Prefix.Should().Be("https://suf.purs.gov.rs/v/?vl=");
    }

    static readonly FiscalVerificationUrl VerificationUrl =
        new(new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });

    [Fact]
    public void StripUrl_removes_every_verification_url_not_only_the_first()
    {
        var stripped = VerificationUrl.StripUrl(
            "lunch https://suf.purs.gov.rs/v/?vl=FIRST00001 and https://suf.purs.gov.rs/v/?vl=SECOND0002 too");

        stripped.Should().NotContain("suf.purs.gov.rs");
        stripped.Should().Be("lunch and too");
    }

    // Important finding (Fable 5.1 review): StripUrl used to run every message through
    // Split(Terminators, RemoveEmptyEntries) + Join(' ') unconditionally, so a URL-free message
    // still had its whole layout flattened to single spaces. A message with no verification URL at
    // all must come back byte-identical.
    [Fact]
    public void StripUrl_returns_the_text_unchanged_when_no_url_is_found()
    {
        const string Text = "кофе 200\n300 такси\tбар";

        var stripped = VerificationUrl.StripUrl(Text);

        stripped.Should().Be(Text);
    }

    // Same finding: when a URL IS found, only the whitespace touching the removed span collapses -
    // a newline further away, between two lines that never mentioned the URL, must survive.
    [Fact]
    public void StripUrl_only_normalises_whitespace_around_a_url_in_a_multiline_message()
    {
        var stripped = VerificationUrl.StripUrl(
            "утро\nhttps://suf.purs.gov.rs/v/?vl=ABC\nобед 300\nужин 400");

        stripped.Should().Be("утро обед 300\nужин 400");
    }

    // Minor finding (Fable 5.1 review): FiscalQrDecoder compares the host case-insensitively
    // (StringComparison.OrdinalIgnoreCase against uri.Host), but TryFind matched Prefix with plain
    // Ordinal, so an upper-cased host in a pasted link would not be found and its verification URL
    // would leak straight into a model prompt. Only the scheme+host part goes case-insensitive - the
    // vl payload after it is base64 and stays byte-exact.
    [Fact]
    public void TryFind_matches_the_scheme_and_host_case_insensitively_but_keeps_the_vl_value_exact()
    {
        var found = VerificationUrl.TryFind("lunch HTTPS://SUF.PURS.GOV.RS/v/?vl=MiXeDCase1 today", out var url);

        found.Should().BeTrue();
        url.Should().Be("HTTPS://SUF.PURS.GOV.RS/v/?vl=MiXeDCase1");
    }
}
