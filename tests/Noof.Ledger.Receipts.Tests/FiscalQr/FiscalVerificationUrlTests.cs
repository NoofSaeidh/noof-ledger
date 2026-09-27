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
