using AwesomeAssertions;
using Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Telegram.Tests;

public class ReceiptLinkDetectorTests
{
    [Fact]
    public void Finds_the_default_verification_link_in_a_captioned_message()
    {
        var detector = new ReceiptLinkDetector(
            new FiscalVerificationUrl(new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" }));

        var found = detector.TryFind("lunch https://suf.purs.gov.rs/v/?vl=AbCdEf123 thanks", out var url);

        found.Should().BeTrue();
        url.Should().Be("https://suf.purs.gov.rs/v/?vl=AbCdEf123");
    }

    // Item E: the detector's prefix comes from configuration (Receipts:VerificationUrlPrefix), so a
    // non-default configured prefix is what it looks for - and the default host no longer matches.
    [Fact]
    public void Honors_a_non_default_configured_verification_url_prefix()
    {
        var detector = new ReceiptLinkDetector(
            new FiscalVerificationUrl(new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://example-tax.example/verify/?vl=" }));

        var foundConfigured = detector.TryFind("lunch https://example-tax.example/verify/?vl=AbCdEf123 thanks", out var url);
        var foundDefault = detector.TryFind("lunch https://suf.purs.gov.rs/v/?vl=AbCdEf123 thanks", out _);

        foundConfigured.Should().BeTrue();
        url.Should().Be("https://example-tax.example/verify/?vl=AbCdEf123");
        foundDefault.Should().BeFalse();
    }
}
