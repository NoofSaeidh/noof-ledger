using AwesomeAssertions;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Receipts.FiscalQr;

namespace Noof.Ledger.Receipts.Tests.FiscalQr;

public class FiscalQrDecoderTests
{
    readonly FiscalQrDecoder decoder = new();

    [Fact]
    public void Decodes_a_sale_receipt()
    {
        var builder = new SyntheticQrPayloadBuilder { TotalAmountRaw = 1_2345_6000, InvoiceType = 0, TransactionType = 0 };
        var url = SyntheticQrPayloadBuilder.UrlFor(builder.Build());

        var result = decoder.Decode(url);

        result.Error.Should().BeNull();
        result.Payload.Should().NotBeNull();
        result.Payload!.Total.Should().Be(12345.6000m);
        result.Payload.Kind.Should().Be(ReceiptKind.Sale);
        result.Payload.VerificationUrl.Should().Be(url);
        result.Payload.IssuedAt.Should().Be(builder.IssuedAt);
        result.Payload.RequestedBy.Should().Be(builder.RequestedBy);
        result.Payload.SignedBy.Should().Be(builder.SignedBy);
        result.Payload.TotalCounter.Should().Be(builder.TotalCounter);
        result.Payload.TransactionTypeCounter.Should().Be(builder.TransactionTypeCounter);
    }

    [Fact]
    public void Decodes_a_refund_receipt()
    {
        var builder = new SyntheticQrPayloadBuilder { InvoiceType = 0, TransactionType = 1 };
        var url = SyntheticQrPayloadBuilder.UrlFor(builder.Build());

        var result = decoder.Decode(url);

        result.Payload!.Kind.Should().Be(ReceiptKind.Refund);
    }

    [Theory]
    [InlineData((byte)1, ReceiptKind.Proforma)]
    [InlineData((byte)2, ReceiptKind.Copy)]
    [InlineData((byte)3, ReceiptKind.Training)]
    [InlineData((byte)4, ReceiptKind.Advance)]
    public void Maps_non_normal_invoice_types(byte invoiceType, ReceiptKind expected)
    {
        var builder = new SyntheticQrPayloadBuilder { InvoiceType = invoiceType, TransactionType = 0 };
        var url = SyntheticQrPayloadBuilder.UrlFor(builder.Build());

        var result = decoder.Decode(url);

        result.Payload!.Kind.Should().Be(expected);
    }

    [Fact]
    public void Decodes_a_receipt_with_a_buyer_id()
    {
        var builder = new SyntheticQrPayloadBuilder { BuyerId = "112233445"u8.ToArray() };
        var url = SyntheticQrPayloadBuilder.UrlFor(builder.Build());

        var result = decoder.Decode(url);

        result.Error.Should().BeNull();
        result.Payload.Should().NotBeNull();
    }

    [Fact]
    public void Decodes_the_short_layout_at_572_bytes()
    {
        var builder = new SyntheticQrPayloadBuilder { LargeEncryptedBlock = false, BuyerId = [] };
        var payload = builder.Build();

        payload.Should().HaveCount(572);
        decoder.Decode(SyntheticQrPayloadBuilder.UrlFor(payload)).Error.Should().BeNull();
    }

    [Fact]
    public void Decodes_the_long_layout_at_848_bytes()
    {
        var builder = new SyntheticQrPayloadBuilder { LargeEncryptedBlock = true, BuyerId = new byte[20] };
        var payload = builder.Build();

        payload.Should().HaveCount(848);
        decoder.Decode(SyntheticQrPayloadBuilder.UrlFor(payload)).Error.Should().BeNull();
    }

    [Fact]
    public void Rejects_a_corrupted_payload_as_an_md5_mismatch()
    {
        var payload = new SyntheticQrPayloadBuilder().Build();
        payload[10] ^= 0xFF;

        var result = decoder.Decode(SyntheticQrPayloadBuilder.UrlFor(payload));

        result.Payload.Should().BeNull();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Rejects_a_url_on_the_wrong_host()
    {
        var payload = new SyntheticQrPayloadBuilder().Build();
        var url = $"https://example.com/v/?vl={Uri.EscapeDataString(Convert.ToBase64String(payload))}";

        var result = decoder.Decode(url);

        result.Payload.Should().BeNull();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Rejects_a_url_missing_the_vl_parameter()
    {
        var result = decoder.Decode("https://suf.purs.gov.rs/v/?other=1");

        result.Payload.Should().BeNull();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Rejects_invalid_base64()
    {
        var result = decoder.Decode("https://suf.purs.gov.rs/v/?vl=not-valid-base64!!!");

        result.Payload.Should().BeNull();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Rejects_a_payload_of_the_wrong_length()
    {
        var url = SyntheticQrPayloadBuilder.UrlFor(new byte[100]);

        var result = decoder.Decode(url);

        result.Payload.Should().BeNull();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Tolerates_a_literal_space_in_the_vl_value_in_place_of_a_plus()
    {
        var payload = new SyntheticQrPayloadBuilder().Build();
        var base64 = Convert.ToBase64String(payload).Replace('+', ' ');
        var url = $"https://suf.purs.gov.rs/v/?vl={base64}";

        var result = decoder.Decode(url);

        result.Error.Should().BeNull();
        result.Payload.Should().NotBeNull();
    }

    [Fact]
    public void Never_throws_on_a_malformed_url()
    {
        var act = () => decoder.Decode("not a url at all");

        act.Should().NotThrow();
    }
}
