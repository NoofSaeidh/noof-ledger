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
}
