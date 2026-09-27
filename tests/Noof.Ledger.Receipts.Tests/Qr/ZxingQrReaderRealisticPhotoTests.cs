using System.Diagnostics;
using AwesomeAssertions;
using Noof.Ledger.Receipts.Qr;
using Noof.Ledger.Receipts.Tests.FiscalQr;

namespace Noof.Ledger.Receipts.Tests.Qr;

// A realistic fiscal QR ("https://suf.purs.gov.rs/v/?vl=<base64>") is dense: the payload built by
// SyntheticQrPayloadBuilder (encrypted block + signature + checksum) is a version-22, 105x105-module
// QR (QrDensityProbe pins this) - far denser than the short test string ZxingQrReaderTests uses.
// These cases reproduce the shape of a phone photo Telegram actually delivers - a compressed,
// <=1280px-long-side JPEG of a fiscal receipt with the QR occupying a modest fraction of the frame -
// which is what exposed the "Vision used: no QR" production failure a low-density synthetic QR never
// would. They pin the reader's current decode boundary at this density: see the measured before/after
// table in this change's commit message and docs/OPEN-QUESTIONS.md for what was tried to push that
// boundary further (more scales, an alternate binarizer, contrast/sharpen, a finder-pattern crop) and
// why none of it moved a single case in either direction - success here is decided by JPEG-quantisation
// phase at encode time, not by anything a decoder can do afterwards.
public class ZxingQrReaderRealisticPhotoTests
{
    static readonly string RealisticVl = SyntheticQrPayloadBuilder.UrlFor(new SyntheticQrPayloadBuilder().Build());

    readonly ZxingQrReader reader = new();

    [Theory]
    [InlineData(0.30, 0)]
    [InlineData(0.29, 0)]
    [InlineData(0.21, 0)]
    [InlineData(0.19, 0)]
    [InlineData(0.18, 0)]
    [InlineData(0.22, 4)]
    public void Decodes_a_compressed_phone_photo_of_a_fiscal_receipt(double qrWidthFraction, double rotationDegrees)
    {
        var jpeg = SyntheticReceiptPhoto.Build(new SyntheticReceiptPhotoOptions
        {
            QrContent = RealisticVl,
            QrWidthFraction = qrWidthFraction,
            RotationDegrees = rotationDegrees,
        });

        using var stream = new MemoryStream(jpeg);
        var stopwatch = Stopwatch.StartNew();
        var result = reader.Read(stream);
        stopwatch.Stop();

        result.Should().Be(RealisticVl);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}
