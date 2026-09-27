using AwesomeAssertions;
using Noof.Ledger.Receipts.Tests.FiscalQr;
using ZXing;
using ZXing.QrCode;

namespace Noof.Ledger.Receipts.Tests.Qr;

// Documents the module density a real fiscal QR renders at, so a future change to
// SyntheticQrPayloadBuilder's field sizes (and therefore to how dense the benchmark photos in
// ZxingQrReaderRealisticPhotoTests are) does not silently drift without anyone noticing. See the
// Phase 6 QR benchmark entry in docs/OPEN-QUESTIONS.md for why this density matters.
public class QrDensityProbe
{
    [Fact]
    public void The_realistic_payload_renders_as_a_dense_version_22_qr()
    {
        var url = SyntheticQrPayloadBuilder.UrlFor(new SyntheticQrPayloadBuilder().Build());
        var matrix = new QRCodeWriter().encode(url, BarcodeFormat.QR_CODE, 0, 0);

        url.Length.Should().Be(796);
        matrix.Width.Should().Be(105);
        matrix.Height.Should().Be(105);
    }
}
