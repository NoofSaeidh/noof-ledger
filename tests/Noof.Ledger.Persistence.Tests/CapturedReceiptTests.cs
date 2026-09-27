using AwesomeAssertions;
using Noof.Ledger.Application.Capture;

namespace Noof.Ledger.Persistence.Tests;

public class CapturedReceiptTests
{
    static readonly DateTimeOffset SentAt = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Refuses_a_photo_id_and_a_verification_url_together()
    {
        var act = () => new CapturedReceipt(111, 5, SentAt, "lunch", "photo-1", "https://suf.purs.gov.rs/v/?vl=AbCdEf123");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Refuses_neither_a_photo_id_nor_a_verification_url()
    {
        var act = () => new CapturedReceipt(111, 5, SentAt, "lunch", null, null);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Accepts_a_photo_id_alone()
    {
        var act = () => new CapturedReceipt(111, 5, SentAt, "lunch", "photo-1", null);

        act.Should().NotThrow();
    }

    [Fact]
    public void Accepts_a_verification_url_alone()
    {
        var act = () => new CapturedReceipt(111, 5, SentAt, "lunch", null, "https://suf.purs.gov.rs/v/?vl=AbCdEf123");

        act.Should().NotThrow();
    }
}
